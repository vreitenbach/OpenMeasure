/**
 * measflow.c — C Reader/Writer for the MeasFlow (.meas) binary format
 *
 * Implements the MeasFlow binary format specification v1.
 * Requires C99 or later.
 */

/* Request POSIX.1-2008 extensions (clock_gettime, strdup, etc.) */
#if !defined(_WIN32) && !defined(_POSIX_C_SOURCE)
#  define _POSIX_C_SOURCE 200809L
#endif

#include "measflow.h"

#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

/* ── Portability / endian helpers ────────────────────────────────────────── */

#if defined(_WIN32)
#  include <winsock2.h>   /* htonl, ntohl */
#  pragma comment(lib, "Ws2_32.lib")
#elif defined(__APPLE__)
#  include <machine/endian.h>
#  include <libkern/OSByteOrder.h>
#  define bswap16(x) OSSwapInt16(x)
#  define bswap32(x) OSSwapInt32(x)
#  define bswap64(x) OSSwapInt64(x)
#else
#  include <endian.h>
#  define bswap16(x) __builtin_bswap16(x)
#  define bswap32(x) __builtin_bswap32(x)
#  define bswap64(x) __builtin_bswap64(x)
#endif

/* Detect host byte order at compile time */
#if defined(__BYTE_ORDER__) && __BYTE_ORDER__ == __ORDER_BIG_ENDIAN__
#  define MEAS_BIG_ENDIAN 1
#elif defined(__BIG_ENDIAN__) || defined(_BIG_ENDIAN)
#  define MEAS_BIG_ENDIAN 1
#else
#  define MEAS_BIG_ENDIAN 0
#endif

/* Read/write little-endian values from/to a byte buffer (§2) */
static uint16_t read_le16(const uint8_t *p) {
    return (uint16_t)(p[0] | (p[1] << 8));
}
static uint32_t read_le32(const uint8_t *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1]<<8) | ((uint32_t)p[2]<<16) | ((uint32_t)p[3]<<24);
}
static uint64_t read_le64(const uint8_t *p) {
    return ((uint64_t)p[0])       | ((uint64_t)p[1] << 8)  |
           ((uint64_t)p[2] << 16) | ((uint64_t)p[3] << 24) |
           ((uint64_t)p[4] << 32) | ((uint64_t)p[5] << 40) |
           ((uint64_t)p[6] << 48) | ((uint64_t)p[7] << 56);
}
static void write_le16(uint8_t *p, uint16_t v) {
    p[0] = (uint8_t)(v);      p[1] = (uint8_t)(v >> 8);
}
static void write_le32(uint8_t *p, uint32_t v) {
    p[0] = (uint8_t)(v);      p[1] = (uint8_t)(v >>  8);
    p[2] = (uint8_t)(v >> 16); p[3] = (uint8_t)(v >> 24);
}
static void write_le64(uint8_t *p, uint64_t v) {
    p[0] = (uint8_t)(v);       p[1] = (uint8_t)(v >>  8);
    p[2] = (uint8_t)(v >> 16); p[3] = (uint8_t)(v >> 24);
    p[4] = (uint8_t)(v >> 32); p[5] = (uint8_t)(v >> 40);
    p[6] = (uint8_t)(v >> 48); p[7] = (uint8_t)(v >> 56);
}

/* float <-> uint32 and double <-> uint64 bit-cast helpers */
static float  le_bytes_to_f32(const uint8_t *p) {
    uint32_t u = read_le32(p); float f; memcpy(&f, &u, 4); return f;
}
static double le_bytes_to_f64(const uint8_t *p) {
    uint64_t u = read_le64(p); double d; memcpy(&d, &u, 8); return d;
}
static void f32_to_le_bytes(uint8_t *p, float  f) {
    uint32_t u; memcpy(&u, &f, 4); write_le32(p, u);
}
static void f64_to_le_bytes(uint8_t *p, double d) {
    uint64_t u; memcpy(&u, &d, 8); write_le64(p, u);
}

/* ── Format constants ─────────────────────────────────────────────────────── */

#define MEAS_MAGIC              UINT32_C(0x5341454D)   /* "MEAS" LE */
#define MEAS_VERSION            1
#define MEAS_FILE_HEADER_SIZE   64
#define MEAS_SEG_HEADER_SIZE    32
#define MEAS_CHUNK_HEADER_SIZE  20   /* int32 + int64 + int64 */
#define MEAS_SEG_TYPE_METADATA  1
#define MEAS_SEG_TYPE_DATA      2

/* ── Dynamic byte buffer ──────────────────────────────────────────────────── */

typedef struct {
    uint8_t *data;
    size_t   size;
    size_t   capacity;
} ByteBuf;

static void bbuf_init(ByteBuf *b) {
    b->data = NULL; b->size = 0; b->capacity = 0;
}
static void bbuf_free(ByteBuf *b) {
    free(b->data); bbuf_init(b);
}
static int bbuf_reserve(ByteBuf *b, size_t extra) {
    size_t need = b->size + extra;
    if (need <= b->capacity) return 1;
    size_t cap = b->capacity ? b->capacity * 2 : 256;
    while (cap < need) cap *= 2;
    uint8_t *p = (uint8_t *)realloc(b->data, cap);
    if (!p) return 0;
    b->data = p; b->capacity = cap;
    return 1;
}
static int bbuf_append(ByteBuf *b, const void *src, size_t n) {
    if (!bbuf_reserve(b, n)) return 0;
    memcpy(b->data + b->size, src, n);
    b->size += n;
    return 1;
}
static int bbuf_append_u8(ByteBuf *b, uint8_t v) {
    return bbuf_append(b, &v, 1);
}
static int bbuf_append_le32(ByteBuf *b, uint32_t v) {
    uint8_t buf[4]; write_le32(buf, v); return bbuf_append(b, buf, 4);
}
static int bbuf_append_le64(ByteBuf *b, uint64_t v) {
    uint8_t buf[8]; write_le64(buf, v); return bbuf_append(b, buf, 8);
}

/* ── String helpers (§2: [int32 byteLength][UTF-8 bytes]) ─────────────────── */

static int bbuf_append_string(ByteBuf *b, const char *s) {
    int32_t len = (int32_t)strlen(s);
    if (!bbuf_append_le32(b, (uint32_t)len)) return 0;
    return bbuf_append(b, s, (size_t)len);
}

/* Read a length-prefixed string from buf[offset].
   Returns newly allocated string and advances *offset; NULL on error. */
static char *decode_string(const uint8_t *buf, size_t bufsize, size_t *offset) {
    if (*offset + 4 > bufsize) return NULL;
    int32_t len = (int32_t)read_le32(buf + *offset);
    *offset += 4;
    if (len < 0 || (size_t)len > bufsize - *offset) return NULL;
    char *s = (char *)malloc((size_t)len + 1);
    if (!s) return NULL;
    memcpy(s, buf + *offset, (size_t)len);
    s[len] = '\0';
    *offset += (size_t)len;
    return s;
}

/* ── Property helpers ─────────────────────────────────────────────────────── */

static void free_property(MeasProperty *p) {
    free(p->key);
    if (p->type == MEAS_STRING) free(p->value.str.data);
    else if (p->type == MEAS_BINARY) free(p->value.bin.data);
    memset(p, 0, sizeof(*p));
}

/* Decode one property from buf at *offset.  Advances *offset.
   Does NOT modify out->key (caller sets it before calling). */
static int decode_property(const uint8_t *buf, size_t bufsz, size_t *off,
                            MeasProperty *out) {
    /* Zero only the value union, not the key pointer set by the caller */
    out->type = MEAS_INT8; /* will be overwritten */
    memset(&out->value, 0, sizeof(out->value));
    if (*off >= bufsz) return 0;
    out->type = (MeasDataType)buf[(*off)++];
    switch (out->type) {
        case MEAS_INT8:
            if (*off + 1 > bufsz) return 0;
            out->value.i8 = (int8_t)buf[(*off)++]; break;
        case MEAS_INT16:
            if (*off + 2 > bufsz) return 0;
            out->value.i16 = (int16_t)read_le16(buf + *off); *off += 2; break;
        case MEAS_INT32:
            if (*off + 4 > bufsz) return 0;
            out->value.i32 = (int32_t)read_le32(buf + *off); *off += 4; break;
        case MEAS_INT64:
        case MEAS_TIMESTAMP:
        case MEAS_TIMESPAN:
            if (*off + 8 > bufsz) return 0;
            out->value.i64 = (int64_t)read_le64(buf + *off); *off += 8; break;
        case MEAS_UINT8:
            if (*off + 1 > bufsz) return 0;
            out->value.u8 = buf[(*off)++]; break;
        case MEAS_UINT16:
            if (*off + 2 > bufsz) return 0;
            out->value.u16 = read_le16(buf + *off); *off += 2; break;
        case MEAS_UINT32:
            if (*off + 4 > bufsz) return 0;
            out->value.u32 = read_le32(buf + *off); *off += 4; break;
        case MEAS_UINT64:
            if (*off + 8 > bufsz) return 0;
            out->value.u64 = read_le64(buf + *off); *off += 8; break;
        case MEAS_FLOAT32:
            if (*off + 4 > bufsz) return 0;
            out->value.f32 = le_bytes_to_f32(buf + *off); *off += 4; break;
        case MEAS_FLOAT64:
            if (*off + 8 > bufsz) return 0;
            out->value.f64 = le_bytes_to_f64(buf + *off); *off += 8; break;
        case MEAS_BOOL:
            if (*off + 1 > bufsz) return 0;
            out->value.bool_val = buf[(*off)++] ? 1 : 0; break;
        case MEAS_STRING: {
            char *s = decode_string(buf, bufsz, off);
            if (!s) return 0;
            out->value.str.data   = s;
            out->value.str.length = (int32_t)strlen(s);
            break;
        }
        case MEAS_BINARY: {
            if (*off + 4 > bufsz) return 0;
            int32_t len = (int32_t)read_le32(buf + *off); *off += 4;
            if (len < 0 || (size_t)len > bufsz - *off) return 0;
            uint8_t *d = (uint8_t *)malloc((size_t)len);
            if (!d && len > 0) return 0;
            if (len > 0) memcpy(d, buf + *off, (size_t)len);
            out->value.bin.data   = d;
            out->value.bin.length = len;
            *off += (size_t)len;
            break;
        }
        default: return 0;
    }
    return 1;
}

/* Decode [int32 count][key,value...] and store into *out_props / *out_count.
   Caller owns the returned array and must free it with free_properties(). */
static int decode_properties(const uint8_t *buf, size_t bufsz, size_t *off,
                              MeasProperty **out_props, int *out_count) {
    *out_props = NULL; *out_count = 0;
    if (*off + 4 > bufsz) return 0;
    int32_t count = (int32_t)read_le32(buf + *off); *off += 4;
    if (count < 0 || count > 100000) return 0;
    if (count == 0) return 1;
    MeasProperty *arr = (MeasProperty *)calloc((size_t)count, sizeof(MeasProperty));
    if (!arr) return 0;
    for (int i = 0; i < count; i++) {
        arr[i].key = decode_string(buf, bufsz, off);
        if (!arr[i].key || !decode_property(buf, bufsz, off, &arr[i])) {
            /* free already-decoded props */
            for (int j = 0; j <= i; j++) free_property(&arr[j]);
            free(arr);
            return 0;
        }
    }
    *out_props = arr; *out_count = count;
    return 1;
}

static void free_properties(MeasProperty *props, int count) {
    for (int i = 0; i < count; i++) free_property(&props[i]);
    free(props);
}

/* ── Statistics helpers ───────────────────────────────────────────────────── */

/* Extract pre-computed stats from channel properties (§13) */
static void extract_stats(MeasChannelData *ch) {
    const char *keys[] = {
        "meas.stats.count", "meas.stats.min", "meas.stats.max",
        "meas.stats.sum",   "meas.stats.mean","meas.stats.variance",
        "meas.stats.first", "meas.stats.last"
    };
    double *fields[] = { NULL, &ch->stats.min, &ch->stats.max,
                         &ch->stats.sum, &ch->stats.mean, &ch->stats.variance,
                         &ch->stats.first, &ch->stats.last };

    for (int i = 0; i < ch->property_count; i++) {
        const MeasProperty *p = &ch->properties[i];
        if (strcmp(p->key, "meas.stats.count") == 0 && p->type == MEAS_INT64) {
            ch->stats.count = p->value.i64;
            ch->has_stats = 1;
        }
        for (int k = 1; k < 8; k++) {
            if (strcmp(p->key, keys[k]) == 0 && p->type == MEAS_FLOAT64) {
                *fields[k] = p->value.f64;
            }
        }
    }
}

/* ── File / segment header I/O ───────────────────────────────────────────── */

/* Write the 64-byte file header into a buffer.
   file_id must be 16 bytes; pass NULL to get a zeroed UUID. */
static void encode_file_header(uint8_t buf[64], int64_t created_at_ns,
                                int64_t segment_count, const uint8_t file_id[16]) {
    memset(buf, 0, 64);
    write_le32(buf +  0, MEAS_MAGIC);
    write_le16(buf +  4, MEAS_VERSION);
    write_le16(buf +  6, 0);                        /* flags */
    write_le64(buf +  8, (uint64_t)MEAS_FILE_HEADER_SIZE); /* first segment offset */
    write_le64(buf + 16, 0);                        /* index offset (reserved) */
    write_le64(buf + 24, (uint64_t)segment_count);
    if (file_id) memcpy(buf + 32, file_id, 16);
    write_le64(buf + 48, (uint64_t)created_at_ns);
    write_le64(buf + 56, 0);                        /* reserved */
}

/* Write a 32-byte segment header into a buffer. */
static void encode_seg_header(uint8_t buf[32], int32_t type, int64_t content_len,
                               int64_t next_seg_offset, int32_t chunk_count) {
    memset(buf, 0, 32);
    write_le32(buf +  0, (uint32_t)type);
    write_le32(buf +  4, 0);                          /* flags */
    write_le64(buf +  8, (uint64_t)content_len);
    write_le64(buf + 16, (uint64_t)next_seg_offset);
    write_le32(buf + 24, (uint32_t)chunk_count);
    write_le32(buf + 28, 0);                          /* CRC32 (not computed) */
}

/* ── Writer internals ─────────────────────────────────────────────────────── */

/* Running statistics accumulator (Welford, §13) */
typedef struct {
    int64_t count;
    double  min, max, sum, mean, m2, first, last;
    int     active; /* 1 if dtype supports stats */
} StatsAcc;

static void stats_update(StatsAcc *s, double v) {
    if (!s->active) return;
    s->count++;
    s->last = v;
    if (s->count == 1) {
        s->first = s->min = s->max = s->sum = s->mean = v; s->m2 = 0.0;
    } else {
        if (v < s->min) s->min = v;
        if (v > s->max) s->max = v;
        s->sum += v;
        double delta = v - s->mean;
        s->mean += delta / (double)s->count;
        s->m2   += delta * (v - s->mean);
    }
}

/* Returns 1 if the data type supports numeric statistics */
static int dtype_supports_stats(MeasDataType dt) {
    switch (dt) {
        case MEAS_INT8:  case MEAS_INT16:  case MEAS_INT32:  case MEAS_INT64:
        case MEAS_UINT8: case MEAS_UINT16: case MEAS_UINT32: case MEAS_UINT64:
        case MEAS_FLOAT32: case MEAS_FLOAT64:
            return 1;
        default:
            return 0;
    }
}

struct MeasChannelWriter {
    char        *name;
    MeasDataType dtype;
    int          global_index;
    ByteBuf      buf;       /* accumulated data bytes (not yet flushed) */
    int64_t      sample_count_pending; /* samples in buf */
    StatsAcc     stats;
};

struct MeasGroupWriter {
    char               *name;
    MeasChannelWriter **channels;
    int                 channel_count;
    int                 channel_cap;
};

struct MeasWriter {
    FILE              *file;
    MeasGroupWriter  **groups;
    int                group_count;
    int                group_cap;
    int                metadata_written;
    int64_t            segment_count;
    int64_t            metadata_content_offset; /* file offset of metadata content */
    int64_t            created_at_ns;
    uint8_t            file_id[16];
};

/* Generate a simple pseudo-random UUID (RFC 4122 v4) without platform deps */
static void gen_uuid(uint8_t out[16]) {
    /* Seed from time; not cryptographic, but sufficient for file IDs */
    uint64_t t = (uint64_t)time(NULL);
    uint64_t a = t ^ (t >> 17) ^ (t << 13) ^ UINT64_C(0x9e3779b97f4a7c15);
    uint64_t b = a ^ (a >> 31) ^ (a << 7)  ^ UINT64_C(0x6c62272e07bb0142);
    memcpy(out,     &a, 8);
    memcpy(out + 8, &b, 8);
    out[6] = (out[6] & 0x0F) | 0x40; /* version 4 */
    out[8] = (out[8] & 0x3F) | 0x80; /* variant 1 */
}

/* Return current time in nanoseconds since Unix epoch */
static int64_t now_nanos(void) {
    struct timespec ts;
#if defined(_WIN32)
    /* Windows: use QueryPerformanceCounter or timespec_get */
    timespec_get(&ts, TIME_UTC);
#else
    clock_gettime(CLOCK_REALTIME, &ts);
#endif
    return (int64_t)ts.tv_sec * INT64_C(1000000000) + ts.tv_nsec;
}

/* Build the metadata segment content for all groups */
static int build_metadata(const MeasWriter *w, ByteBuf *b, int with_stats) {
    if (!bbuf_append_le32(b, (uint32_t)w->group_count)) return 0;

    for (int gi = 0; gi < w->group_count; gi++) {
        const MeasGroupWriter *g = w->groups[gi];
        if (!bbuf_append_string(b, g->name)) return 0;
        /* group properties: 0 for now */
        if (!bbuf_append_le32(b, 0)) return 0;
        if (!bbuf_append_le32(b, (uint32_t)g->channel_count)) return 0;

        for (int ci = 0; ci < g->channel_count; ci++) {
            const MeasChannelWriter *ch = g->channels[ci];
            if (!bbuf_append_string(b, ch->name)) return 0;
            if (!bbuf_append_u8(b, (uint8_t)ch->dtype)) return 0;

            /* channel properties = statistics if applicable */
            if (with_stats && ch->stats.active && ch->stats.count > 0) {
                /* [int32: propCount = 8] [key,value...] */
                if (!bbuf_append_le32(b, 8)) return 0;
                double variance = ch->stats.count > 0
                    ? ch->stats.m2 / (double)ch->stats.count : 0.0;
                struct { const char *k; MeasDataType t; double dv; int64_t iv; } e[] = {
                    {"meas.stats.count",    MEAS_INT64,   0.0,              ch->stats.count},
                    {"meas.stats.min",      MEAS_FLOAT64, ch->stats.min,    0},
                    {"meas.stats.max",      MEAS_FLOAT64, ch->stats.max,    0},
                    {"meas.stats.sum",      MEAS_FLOAT64, ch->stats.sum,    0},
                    {"meas.stats.mean",     MEAS_FLOAT64, ch->stats.mean,   0},
                    {"meas.stats.variance", MEAS_FLOAT64, variance,         0},
                    {"meas.stats.first",    MEAS_FLOAT64, ch->stats.first,  0},
                    {"meas.stats.last",     MEAS_FLOAT64, ch->stats.last,   0},
                };
                for (int k = 0; k < 8; k++) {
                    if (!bbuf_append_string(b, e[k].k)) return 0;
                    if (e[k].t == MEAS_INT64) {
                        if (!bbuf_append_u8(b, (uint8_t)MEAS_INT64)) return 0;
                        if (!bbuf_append_le64(b, (uint64_t)e[k].iv)) return 0;
                    } else {
                        if (!bbuf_append_u8(b, (uint8_t)MEAS_FLOAT64)) return 0;
                        uint8_t vb[8]; f64_to_le_bytes(vb, e[k].dv);
                        if (!bbuf_append(b, vb, 8)) return 0;
                    }
                }
            } else if (with_stats && ch->stats.active) {
                /* Write placeholder zeroed stats (same byte count) */
                if (!bbuf_append_le32(b, 8)) return 0;
                struct { const char *k; MeasDataType t; } e[] = {
                    {"meas.stats.count",    MEAS_INT64},
                    {"meas.stats.min",      MEAS_FLOAT64},
                    {"meas.stats.max",      MEAS_FLOAT64},
                    {"meas.stats.sum",      MEAS_FLOAT64},
                    {"meas.stats.mean",     MEAS_FLOAT64},
                    {"meas.stats.variance", MEAS_FLOAT64},
                    {"meas.stats.first",    MEAS_FLOAT64},
                    {"meas.stats.last",     MEAS_FLOAT64},
                };
                for (int k = 0; k < 8; k++) {
                    if (!bbuf_append_string(b, e[k].k)) return 0;
                    if (e[k].t == MEAS_INT64) {
                        if (!bbuf_append_u8(b, (uint8_t)MEAS_INT64)) return 0;
                        if (!bbuf_append_le64(b, 0)) return 0;
                    } else {
                        if (!bbuf_append_u8(b, (uint8_t)MEAS_FLOAT64)) return 0;
                        uint8_t z[8] = {0}; if (!bbuf_append(b, z, 8)) return 0;
                    }
                }
            } else {
                /* no properties */
                if (!bbuf_append_le32(b, 0)) return 0;
            }
        }
    }
    return 1;
}

/* Write a segment (header + content) at the current file position.
   Patches NextSegmentOffset in the header.
   Increments w->segment_count.
   Returns 0 on error. */
static int write_segment(MeasWriter *w, int32_t type, const uint8_t *content,
                          size_t content_len, int32_t chunk_count) {
    long seg_start = ftell(w->file);
    if (seg_start < 0) return 0;

    uint8_t hdr[32];
    encode_seg_header(hdr, type, (int64_t)content_len, 0 /* patched */, chunk_count);
    if (fwrite(hdr, 1, 32, w->file) != 32) return 0;
    if (content_len > 0) {
        if (fwrite(content, 1, content_len, w->file) != content_len) return 0;
    }
    long next_off = ftell(w->file);
    if (next_off < 0) return 0;

    /* Patch NextSegmentOffset in the already-written segment header */
    encode_seg_header(hdr, type, (int64_t)content_len, (int64_t)next_off, chunk_count);
    if (fseek(w->file, seg_start, SEEK_SET) != 0) return 0;
    if (fwrite(hdr, 1, 32, w->file) != 32) return 0;
    if (fseek(w->file, next_off, SEEK_SET) != 0) return 0;

    w->segment_count++;
    return 1;
}

static int ensure_metadata(MeasWriter *w) {
    if (w->metadata_written) return 1;
    w->metadata_written = 1;

    /* Assign global channel indices */
    int idx = 0;
    for (int gi = 0; gi < w->group_count; gi++)
        for (int ci = 0; ci < w->groups[gi]->channel_count; ci++)
            w->groups[gi]->channels[ci]->global_index = idx++;

    /* Write actual file header (replaces 64-byte placeholder) */
    uint8_t fhdr[64];
    encode_file_header(fhdr, w->created_at_ns, 0, w->file_id);
    if (fseek(w->file, 0, SEEK_SET) != 0) return 0;
    if (fwrite(fhdr, 1, 64, w->file) != 64) return 0;
    if (fseek(w->file, 0, SEEK_END) != 0) return 0;

    /* Build and write metadata segment with placeholder stats */
    ByteBuf meta; bbuf_init(&meta);
    if (!build_metadata(w, &meta, 1 /* with placeholder stats */)) {
        bbuf_free(&meta); return 0;
    }
    w->metadata_content_offset = ftell(w->file) + MEAS_SEG_HEADER_SIZE;
    int ok = write_segment(w, MEAS_SEG_TYPE_METADATA, meta.data, meta.size, 0);
    bbuf_free(&meta);
    return ok;
}

/* ── Writer public API ────────────────────────────────────────────────────── */

MeasWriter *meas_writer_open(const char *path) {
    FILE *f = fopen(path, "wb");
    if (!f) return NULL;

    MeasWriter *w = (MeasWriter *)calloc(1, sizeof(MeasWriter));
    if (!w) { fclose(f); return NULL; }

    w->file = f;
    w->created_at_ns = now_nanos();
    gen_uuid(w->file_id);

    /* Write 64-byte placeholder so the file header can be patched later */
    uint8_t placeholder[64] = {0};
    if (fwrite(placeholder, 1, 64, f) != 64) {
        fclose(f); free(w); return NULL;
    }
    return w;
}

MeasGroupWriter *meas_writer_add_group(MeasWriter *writer, const char *name) {
    if (!writer || writer->metadata_written) return NULL;

    if (writer->group_count == writer->group_cap) {
        int cap = writer->group_cap ? writer->group_cap * 2 : 4;
        MeasGroupWriter **p = (MeasGroupWriter **)realloc(
            writer->groups, (size_t)cap * sizeof(*p));
        if (!p) return NULL;
        writer->groups = p; writer->group_cap = cap;
    }
    MeasGroupWriter *g = (MeasGroupWriter *)calloc(1, sizeof(MeasGroupWriter));
    if (!g) return NULL;
    g->name = strdup(name);
    if (!g->name) { free(g); return NULL; }

    writer->groups[writer->group_count++] = g;
    return g;
}

MeasChannelWriter *meas_group_add_channel(MeasGroupWriter *group, const char *name,
                                           MeasDataType dtype) {
    if (!group) return NULL;
    if (group->channel_count == group->channel_cap) {
        int cap = group->channel_cap ? group->channel_cap * 2 : 4;
        MeasChannelWriter **p = (MeasChannelWriter **)realloc(
            group->channels, (size_t)cap * sizeof(*p));
        if (!p) return NULL;
        group->channels = p; group->channel_cap = cap;
    }
    MeasChannelWriter *ch = (MeasChannelWriter *)calloc(1, sizeof(MeasChannelWriter));
    if (!ch) return NULL;
    ch->name  = strdup(name);
    if (!ch->name) { free(ch); return NULL; }
    ch->dtype = dtype;
    ch->stats.active = dtype_supports_stats(dtype);
    bbuf_init(&ch->buf);

    group->channels[group->channel_count++] = ch;
    return ch;
}

int meas_writer_flush(MeasWriter *writer) {
    if (!writer) return -1;
    if (!ensure_metadata(writer)) return -1;

    /* Count pending channels */
    int pending = 0;
    for (int gi = 0; gi < writer->group_count; gi++)
        for (int ci = 0; ci < writer->groups[gi]->channel_count; ci++)
            if (writer->groups[gi]->channels[ci]->buf.size > 0) pending++;
    if (pending == 0) return 0;

    /* Build data segment content:
       [int32: chunkCount]
       For each pending channel:
         [int32: channelIndex][int64: sampleCount][int64: dataByteLen][bytes: data]
    */
    ByteBuf seg; bbuf_init(&seg);
    if (!bbuf_append_le32(&seg, (uint32_t)pending)) { bbuf_free(&seg); return -1; }

    for (int gi = 0; gi < writer->group_count; gi++) {
        for (int ci = 0; ci < writer->groups[gi]->channel_count; ci++) {
            MeasChannelWriter *ch = writer->groups[gi]->channels[ci];
            if (ch->buf.size == 0) continue;
            /* Chunk header */
            if (!bbuf_append_le32(&seg, (uint32_t)ch->global_index)) goto fail;
            if (!bbuf_append_le64(&seg, (uint64_t)ch->sample_count_pending)) goto fail;
            if (!bbuf_append_le64(&seg, (uint64_t)ch->buf.size)) goto fail;
            if (!bbuf_append(&seg, ch->buf.data, ch->buf.size)) goto fail;
            /* Reset buffer */
            ch->buf.size = 0;
            ch->sample_count_pending = 0;
        }
    }

    int ok = write_segment(writer, MEAS_SEG_TYPE_DATA, seg.data, seg.size, pending);
    bbuf_free(&seg);
    if (fflush(writer->file) != 0) return -1;
    return ok ? 0 : -1;

fail:
    bbuf_free(&seg); return -1;
}

void meas_writer_close(MeasWriter *writer) {
    if (!writer) return;

    /* Flush any remaining data */
    meas_writer_flush(writer);

    /* Patch metadata segment in-place with final statistics */
    if (writer->metadata_written && writer->metadata_content_offset > 0) {
        ByteBuf final_meta; bbuf_init(&final_meta);
        if (build_metadata(writer, &final_meta, 1 /* with real stats */)) {
            fseek(writer->file, (long)writer->metadata_content_offset, SEEK_SET);
            fwrite(final_meta.data, 1, final_meta.size, writer->file);
            fseek(writer->file, 0, SEEK_END);
        }
        bbuf_free(&final_meta);
    }

    /* Patch file header with final segment count */
    uint8_t fhdr[64];
    encode_file_header(fhdr, writer->created_at_ns, writer->segment_count, writer->file_id);
    fseek(writer->file, 0, SEEK_SET);
    fwrite(fhdr, 1, 64, writer->file);

    fclose(writer->file);

    /* Free groups and channels */
    for (int gi = 0; gi < writer->group_count; gi++) {
        MeasGroupWriter *g = writer->groups[gi];
        for (int ci = 0; ci < g->channel_count; ci++) {
            MeasChannelWriter *ch = g->channels[ci];
            free(ch->name);
            bbuf_free(&ch->buf);
            free(ch);
        }
        free(g->channels);
        free(g->name);
        free(g);
    }
    free(writer->groups);
    free(writer);
}

/* ── Typed write helpers ──────────────────────────────────────────────────── */

/* For float/double we still need LE encoding; on little-endian it's a no-op
   but we handle it explicitly for correctness. */
int meas_channel_write_f32(MeasChannelWriter *ch, const float *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_FLOAT32) return -1;
    if (!bbuf_reserve(&ch->buf, (size_t)(count * 4))) return -1;
    for (int64_t i = 0; i < count; i++) {
        uint8_t b[4]; f32_to_le_bytes(b, data[i]);
        if (!bbuf_append(&ch->buf, b, 4)) return -1;
        stats_update(&ch->stats, (double)data[i]);
    }
    ch->sample_count_pending += count;
    return 0;
}
int meas_channel_write_f64(MeasChannelWriter *ch, const double *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_FLOAT64) return -1;
    if (!bbuf_reserve(&ch->buf, (size_t)(count * 8))) return -1;
    for (int64_t i = 0; i < count; i++) {
        uint8_t b[8]; f64_to_le_bytes(b, data[i]);
        if (!bbuf_append(&ch->buf, b, 8)) return -1;
        stats_update(&ch->stats, data[i]);
    }
    ch->sample_count_pending += count;
    return 0;
}
int meas_channel_write_i8(MeasChannelWriter *ch, const int8_t *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_INT8) return -1;
    if (!bbuf_append(&ch->buf, data, (size_t)count)) return -1;
    for (int64_t i = 0; i < count; i++) stats_update(&ch->stats, (double)data[i]);
    ch->sample_count_pending += count; return 0;
}
int meas_channel_write_i16(MeasChannelWriter *ch, const int16_t *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_INT16) return -1;
    if (!bbuf_reserve(&ch->buf, (size_t)(count * 2))) return -1;
    for (int64_t i = 0; i < count; i++) {
        uint8_t b[2]; write_le16(b, (uint16_t)data[i]);
        bbuf_append(&ch->buf, b, 2);
        stats_update(&ch->stats, (double)data[i]);
    }
    ch->sample_count_pending += count; return 0;
}
int meas_channel_write_i32(MeasChannelWriter *ch, const int32_t *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_INT32) return -1;
    if (!bbuf_reserve(&ch->buf, (size_t)(count * 4))) return -1;
    for (int64_t i = 0; i < count; i++) {
        uint8_t b[4]; write_le32(b, (uint32_t)data[i]);
        bbuf_append(&ch->buf, b, 4);
        stats_update(&ch->stats, (double)data[i]);
    }
    ch->sample_count_pending += count; return 0;
}
int meas_channel_write_i64(MeasChannelWriter *ch, const int64_t *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_INT64) return -1;
    if (!bbuf_reserve(&ch->buf, (size_t)(count * 8))) return -1;
    for (int64_t i = 0; i < count; i++) {
        uint8_t b[8]; write_le64(b, (uint64_t)data[i]);
        bbuf_append(&ch->buf, b, 8);
        stats_update(&ch->stats, (double)data[i]);
    }
    ch->sample_count_pending += count; return 0;
}
int meas_channel_write_u8(MeasChannelWriter *ch, const uint8_t *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_UINT8) return -1;
    if (!bbuf_append(&ch->buf, data, (size_t)count)) return -1;
    for (int64_t i = 0; i < count; i++) stats_update(&ch->stats, (double)data[i]);
    ch->sample_count_pending += count; return 0;
}
int meas_channel_write_u16(MeasChannelWriter *ch, const uint16_t *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_UINT16) return -1;
    if (!bbuf_reserve(&ch->buf, (size_t)(count * 2))) return -1;
    for (int64_t i = 0; i < count; i++) {
        uint8_t b[2]; write_le16(b, data[i]);
        bbuf_append(&ch->buf, b, 2);
        stats_update(&ch->stats, (double)data[i]);
    }
    ch->sample_count_pending += count; return 0;
}
int meas_channel_write_u32(MeasChannelWriter *ch, const uint32_t *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_UINT32) return -1;
    if (!bbuf_reserve(&ch->buf, (size_t)(count * 4))) return -1;
    for (int64_t i = 0; i < count; i++) {
        uint8_t b[4]; write_le32(b, data[i]);
        bbuf_append(&ch->buf, b, 4);
        stats_update(&ch->stats, (double)data[i]);
    }
    ch->sample_count_pending += count; return 0;
}
int meas_channel_write_u64(MeasChannelWriter *ch, const uint64_t *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_UINT64) return -1;
    if (!bbuf_reserve(&ch->buf, (size_t)(count * 8))) return -1;
    for (int64_t i = 0; i < count; i++) {
        uint8_t b[8]; write_le64(b, data[i]);
        bbuf_append(&ch->buf, b, 8);
        stats_update(&ch->stats, (double)data[i]);
    }
    ch->sample_count_pending += count; return 0;
}
int meas_channel_write_timestamp(MeasChannelWriter *ch, const int64_t *ns, int64_t count) {
    if (!ch || (ch->dtype != MEAS_TIMESTAMP && ch->dtype != MEAS_TIMESPAN)) return -1;
    if (!bbuf_reserve(&ch->buf, (size_t)(count * 8))) return -1;
    for (int64_t i = 0; i < count; i++) {
        uint8_t b[8]; write_le64(b, (uint64_t)ns[i]);
        bbuf_append(&ch->buf, b, 8);
    }
    ch->sample_count_pending += count; return 0;
}
int meas_channel_write_frame(MeasChannelWriter *ch, const uint8_t *frame, int32_t length) {
    if (!ch || ch->dtype != MEAS_BINARY) return -1;
    uint8_t len_buf[4]; write_le32(len_buf, (uint32_t)length);
    if (!bbuf_append(&ch->buf, len_buf, 4)) return -1;
    if (length > 0 && !bbuf_append(&ch->buf, frame, (size_t)length)) return -1;
    ch->sample_count_pending++; return 0;
}

/* Single-value wrappers */
int meas_channel_write_f32_one(MeasChannelWriter *ch, float    v) { return meas_channel_write_f32(ch, &v, 1); }
int meas_channel_write_f64_one(MeasChannelWriter *ch, double   v) { return meas_channel_write_f64(ch, &v, 1); }
int meas_channel_write_i32_one(MeasChannelWriter *ch, int32_t  v) { return meas_channel_write_i32(ch, &v, 1); }
int meas_channel_write_i64_one(MeasChannelWriter *ch, int64_t  v) { return meas_channel_write_i64(ch, &v, 1); }
int meas_channel_write_bool_one(MeasChannelWriter *ch, int     v) {
    if (!ch || ch->dtype != MEAS_BOOL) return -1;
    uint8_t b = v ? 1 : 0;
    if (!bbuf_append(&ch->buf, &b, 1)) return -1;
    ch->sample_count_pending++; return 0;
}

/* ── Reader internals ────────────────────────────────────────────────────── */

struct MeasReader {
    MeasGroupData *groups;
    int            group_count;
};

/* Append raw bytes to a channel's data array */
static int channel_append_data(MeasChannelData *ch, const uint8_t *src, size_t len) {
    uint8_t *p = (uint8_t *)realloc(ch->data, (size_t)ch->data_size + len);
    if (!p) return 0;
    memcpy(p + ch->data_size, src, len);
    ch->data = p;
    ch->data_size += (int64_t)len;
    return 1;
}

/* Decode metadata segment content (§6) and populate reader groups */
static int decode_metadata_segment(MeasReader *r, const uint8_t *buf, size_t bufsz) {
    size_t off = 0;
    if (off + 4 > bufsz) return 0;
    int32_t group_count = (int32_t)read_le32(buf + off); off += 4;
    if (group_count < 0 || group_count > 100000) return 0;

    r->groups = (MeasGroupData *)calloc((size_t)group_count, sizeof(MeasGroupData));
    if (!r->groups && group_count > 0) return 0;
    r->group_count = group_count;

    for (int gi = 0; gi < group_count; gi++) {
        MeasGroupData *g = &r->groups[gi];
        g->name = decode_string(buf, bufsz, &off);
        if (!g->name) return 0;
        if (!decode_properties(buf, bufsz, &off, &g->properties, &g->property_count)) return 0;

        if (off + 4 > bufsz) return 0;
        int32_t ch_count = (int32_t)read_le32(buf + off); off += 4;
        if (ch_count < 0 || ch_count > 100000) return 0;

        g->channels = (MeasChannelData *)calloc((size_t)ch_count, sizeof(MeasChannelData));
        if (!g->channels && ch_count > 0) return 0;
        g->channel_count = ch_count;

        for (int ci = 0; ci < ch_count; ci++) {
            MeasChannelData *ch = &g->channels[ci];
            ch->name = decode_string(buf, bufsz, &off);
            if (!ch->name) return 0;
            if (off >= bufsz) return 0;
            ch->data_type = (MeasDataType)buf[off++];
            if (!decode_properties(buf, bufsz, &off, &ch->properties, &ch->property_count)) return 0;
            extract_stats(ch);
        }
    }
    return 1;
}

/* Process a data segment (§7).
   all_channels is a flat array indexed by global channel index. */
static int decode_data_segment(const uint8_t *buf, size_t bufsz,
                                MeasChannelData **all_channels, int total_channels) {
    size_t off = 0;
    if (off + 4 > bufsz) return 0;
    int32_t chunk_count = (int32_t)read_le32(buf + off); off += 4;

    for (int i = 0; i < chunk_count; i++) {
        if (off + MEAS_CHUNK_HEADER_SIZE > bufsz) return 0;
        int32_t  ch_idx  = (int32_t) read_le32(buf + off); off += 4;
        int64_t  samples = (int64_t)  read_le64(buf + off); off += 8;
        int64_t  data_len = (int64_t) read_le64(buf + off); off += 8;

        if (data_len < 0 || (size_t)data_len > bufsz - off) return 0;
        if (ch_idx >= 0 && ch_idx < total_channels && all_channels[ch_idx]) {
            MeasChannelData *ch = all_channels[ch_idx];
            if (!channel_append_data(ch, buf + off, (size_t)data_len)) return 0;
            ch->sample_count += samples;
        }
        off += (size_t)data_len;
    }
    return 1;
}

/* ── Reader public API ───────────────────────────────────────────────────── */

MeasReader *meas_reader_open(const char *path) {
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;

    /* Read entire file into memory */
    if (fseek(f, 0, SEEK_END) != 0) { fclose(f); return NULL; }
    long fsz_l = ftell(f);
    if (fsz_l < 0) { fclose(f); return NULL; }
    size_t fsz = (size_t)fsz_l;
    rewind(f);
    uint8_t *filebuf = (uint8_t *)malloc(fsz);
    if (!filebuf) { fclose(f); return NULL; }
    if (fread(filebuf, 1, fsz, f) != fsz) { free(filebuf); fclose(f); return NULL; }
    fclose(f);

    /* Validate file header (§4) */
    if (fsz < MEAS_FILE_HEADER_SIZE) { free(filebuf); return NULL; }
    uint32_t magic = read_le32(filebuf);
    if (magic != MEAS_MAGIC) { free(filebuf); return NULL; }
    uint16_t version = read_le16(filebuf + 4);
    if (version != MEAS_VERSION) { free(filebuf); return NULL; }

    int64_t first_seg_off  = (int64_t)read_le64(filebuf + 8);
    int64_t segment_count_hdr = (int64_t)read_le64(filebuf + 24);

    MeasReader *r = (MeasReader *)calloc(1, sizeof(MeasReader));
    if (!r) { free(filebuf); return NULL; }

    /* Build a flat channel index — will be filled after metadata is decoded */
    MeasChannelData **flat_channels = NULL;
    int total_channels = 0;

    /* Walk segment chain (§5) */
    int64_t offset = first_seg_off;
    int64_t max_seg = segment_count_hdr > 0 ? segment_count_hdr : INT64_MAX;

    for (int64_t si = 0; si < max_seg; si++) {
        if (offset < 0 || (size_t)offset + MEAS_SEG_HEADER_SIZE > fsz) break;

        const uint8_t *shdr = filebuf + offset;
        int32_t seg_type    = (int32_t)read_le32(shdr);
        int64_t content_len = (int64_t)read_le64(shdr + 8);
        int64_t next_off    = (int64_t)read_le64(shdr + 16);

        size_t content_start = (size_t)offset + MEAS_SEG_HEADER_SIZE;
        if (content_len < 0 || content_start + (size_t)content_len > fsz) break;

        const uint8_t *content = filebuf + content_start;
        size_t         content_sz = (size_t)content_len;

        if (seg_type == MEAS_SEG_TYPE_METADATA) {
            /* Decode metadata first so channels exist for data segments */
            decode_metadata_segment(r, content, content_sz);
            /* Build flat channel index */
            total_channels = 0;
            for (int gi = 0; gi < r->group_count; gi++)
                total_channels += r->groups[gi].channel_count;
            flat_channels = (MeasChannelData **)calloc(
                (size_t)(total_channels > 0 ? total_channels : 1), sizeof(*flat_channels));
            if (!flat_channels) break;
            int idx = 0;
            for (int gi = 0; gi < r->group_count; gi++)
                for (int ci = 0; ci < r->groups[gi].channel_count; ci++)
                    flat_channels[idx++] = &r->groups[gi].channels[ci];
        } else if (seg_type == MEAS_SEG_TYPE_DATA && flat_channels) {
            decode_data_segment(content, content_sz, flat_channels, total_channels);
        }

        if (next_off <= offset) break; /* end of chain (§5) */
        offset = next_off;
    }

    free(flat_channels);
    free(filebuf);
    return r;
}

void meas_reader_close(MeasReader *reader) {
    if (!reader) return;
    for (int gi = 0; gi < reader->group_count; gi++) {
        MeasGroupData *g = &reader->groups[gi];
        for (int ci = 0; ci < g->channel_count; ci++) {
            MeasChannelData *ch = &g->channels[ci];
            free(ch->name);
            free(ch->data);
            free_properties(ch->properties, ch->property_count);
        }
        free(g->channels);
        free(g->name);
        free_properties(g->properties, g->property_count);
    }
    free(reader->groups);
    free(reader);
}

int meas_reader_group_count(const MeasReader *reader) {
    return reader ? reader->group_count : 0;
}
const MeasGroupData *meas_reader_group(const MeasReader *reader, int idx) {
    if (!reader || idx < 0 || idx >= reader->group_count) return NULL;
    return &reader->groups[idx];
}
const MeasGroupData *meas_reader_group_by_name(const MeasReader *reader, const char *name) {
    if (!reader || !name) return NULL;
    for (int i = 0; i < reader->group_count; i++)
        if (strcmp(reader->groups[i].name, name) == 0)
            return &reader->groups[i];
    return NULL;
}
const MeasChannelData *meas_group_channel_by_name(const MeasGroupData *g, const char *name) {
    if (!g || !name) return NULL;
    for (int i = 0; i < g->channel_count; i++)
        if (strcmp(g->channels[i].name, name) == 0)
            return &g->channels[i];
    return NULL;
}

/* ── Typed read helpers ──────────────────────────────────────────────────── */

/* For fixed-size LE types: just copy bytes, with possible byte-swap on BE hosts */
static int64_t read_fixed(const MeasChannelData *ch, void *out,
                           MeasDataType expected, int elem_size, int64_t max_count) {
    if (!ch || ch->data_type != expected || !out) return -1;
    int64_t n = ch->sample_count < max_count ? ch->sample_count : max_count;
    /* Data is already stored as LE bytes; on LE hosts we can memcpy directly */
#if !MEAS_BIG_ENDIAN
    memcpy(out, ch->data, (size_t)(n * elem_size));
#else
    /* Big-endian: swap each element */
    for (int64_t i = 0; i < n; i++) {
        const uint8_t *src = ch->data + i * elem_size;
        uint8_t *dst = (uint8_t *)out + i * elem_size;
        for (int b = 0; b < elem_size; b++) dst[b] = src[elem_size - 1 - b];
    }
#endif
    return n;
}

int64_t meas_channel_read_f32(const MeasChannelData *ch, float    *out, int64_t max_count) {
    if (!ch || ch->data_type != MEAS_FLOAT32 || !out) return -1;
    int64_t n = ch->sample_count < max_count ? ch->sample_count : max_count;
    for (int64_t i = 0; i < n; i++) out[i] = le_bytes_to_f32(ch->data + i * 4);
    return n;
}
int64_t meas_channel_read_f64(const MeasChannelData *ch, double   *out, int64_t max_count) {
    if (!ch || ch->data_type != MEAS_FLOAT64 || !out) return -1;
    int64_t n = ch->sample_count < max_count ? ch->sample_count : max_count;
    for (int64_t i = 0; i < n; i++) out[i] = le_bytes_to_f64(ch->data + i * 8);
    return n;
}
int64_t meas_channel_read_i8 (const MeasChannelData *ch, int8_t   *out, int64_t max_count) {
    return read_fixed(ch, out, MEAS_INT8,   1, max_count); }
int64_t meas_channel_read_i16(const MeasChannelData *ch, int16_t  *out, int64_t max_count) {
    if (!ch || ch->data_type != MEAS_INT16 || !out) return -1;
    int64_t n = ch->sample_count < max_count ? ch->sample_count : max_count;
    for (int64_t i = 0; i < n; i++) out[i] = (int16_t)read_le16(ch->data + i * 2);
    return n;
}
int64_t meas_channel_read_i32(const MeasChannelData *ch, int32_t  *out, int64_t max_count) {
    if (!ch || ch->data_type != MEAS_INT32 || !out) return -1;
    int64_t n = ch->sample_count < max_count ? ch->sample_count : max_count;
    for (int64_t i = 0; i < n; i++) out[i] = (int32_t)read_le32(ch->data + i * 4);
    return n;
}
int64_t meas_channel_read_i64(const MeasChannelData *ch, int64_t  *out, int64_t max_count) {
    if (!ch || ch->data_type != MEAS_INT64 || !out) return -1;
    int64_t n = ch->sample_count < max_count ? ch->sample_count : max_count;
    for (int64_t i = 0; i < n; i++) out[i] = (int64_t)read_le64(ch->data + i * 8);
    return n;
}
int64_t meas_channel_read_u8 (const MeasChannelData *ch, uint8_t  *out, int64_t max_count) {
    return read_fixed(ch, out, MEAS_UINT8,  1, max_count); }
int64_t meas_channel_read_u16(const MeasChannelData *ch, uint16_t *out, int64_t max_count) {
    if (!ch || ch->data_type != MEAS_UINT16 || !out) return -1;
    int64_t n = ch->sample_count < max_count ? ch->sample_count : max_count;
    for (int64_t i = 0; i < n; i++) out[i] = read_le16(ch->data + i * 2);
    return n;
}
int64_t meas_channel_read_u32(const MeasChannelData *ch, uint32_t *out, int64_t max_count) {
    if (!ch || ch->data_type != MEAS_UINT32 || !out) return -1;
    int64_t n = ch->sample_count < max_count ? ch->sample_count : max_count;
    for (int64_t i = 0; i < n; i++) out[i] = read_le32(ch->data + i * 4);
    return n;
}
int64_t meas_channel_read_u64(const MeasChannelData *ch, uint64_t *out, int64_t max_count) {
    if (!ch || ch->data_type != MEAS_UINT64 || !out) return -1;
    int64_t n = ch->sample_count < max_count ? ch->sample_count : max_count;
    for (int64_t i = 0; i < n; i++) out[i] = read_le64(ch->data + i * 8);
    return n;
}
int64_t meas_channel_read_timestamp(const MeasChannelData *ch, int64_t *out_ns, int64_t max_count) {
    if (!ch || (ch->data_type != MEAS_TIMESTAMP && ch->data_type != MEAS_TIMESPAN) || !out_ns)
        return -1;
    int64_t n = ch->sample_count < max_count ? ch->sample_count : max_count;
    for (int64_t i = 0; i < n; i++) out_ns[i] = (int64_t)read_le64(ch->data + i * 8);
    return n;
}

int meas_channel_next_frame(const MeasChannelData *ch, int64_t *state,
                             const uint8_t **frame_data, int32_t *frame_length) {
    if (!ch || !state || !frame_data || !frame_length) return -1;
    if (ch->data_type != MEAS_BINARY && ch->data_type != MEAS_STRING) return -1;
    if (*state >= ch->data_size) return 0;
    if (*state + 4 > ch->data_size) return -1;
    int32_t len = (int32_t)read_le32(ch->data + (size_t)*state);
    *state += 4;
    if (len < 0 || *state + len > ch->data_size) return -1;
    *frame_data   = ch->data + (size_t)*state;
    *frame_length = len;
    *state += len;
    return 1;
}
