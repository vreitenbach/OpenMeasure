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
#ifdef _MSC_VER
#  define strdup _strdup
#endif

#include "measflow.h"

#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

/* Memory-mapped I/O headers (POSIX only; Windows uses winsock2.h/windows.h below) */
#ifndef _WIN32
#  include <sys/mman.h>
#  include <sys/stat.h>
#  include <fcntl.h>
#  include <unistd.h>
#endif

#ifdef MEAS_HAVE_LZ4
#  include <lz4.h>
#endif
#ifdef MEAS_HAVE_ZSTD
#  include <zstd.h>
#endif

/* ── Portability / endian helpers ────────────────────────────────────────── */

#if defined(_WIN32)
#  include <winsock2.h>   /* htonl, ntohl — must precede windows.h */
#  include <windows.h>    /* CreateFileMapping, MapViewOfFile */
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
/* f32_to_le_bytes: only used in big-endian write path; on LE systems the
   compiler may warn about it being unused, so suppress that. */
#ifdef __GNUC__
__attribute__((unused))
#endif
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
    size_t cap = b->capacity ? b->capacity * 2 : 4096;
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
/* Append string with known length (avoids strlen on constant strings) */
static int bbuf_append_string_n(ByteBuf *b, const char *s, size_t len) {
    if (!bbuf_append_le32(b, (uint32_t)len)) return 0;
    return bbuf_append(b, s, len);
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
                                int64_t segment_count, const uint8_t file_id[16],
                                uint16_t flags) {
    memset(buf, 0, 64);
    write_le32(buf +  0, MEAS_MAGIC);
    write_le16(buf +  4, MEAS_VERSION);
    write_le16(buf +  6, flags);                     /* flags */
    write_le64(buf +  8, (uint64_t)MEAS_FILE_HEADER_SIZE); /* first segment offset */
    write_le64(buf + 16, 0);                        /* index offset (reserved) */
    write_le64(buf + 24, (uint64_t)segment_count);
    if (file_id) memcpy(buf + 32, file_id, 16);
    write_le64(buf + 48, (uint64_t)created_at_ns);
    write_le64(buf + 56, 0);                        /* reserved */
}

/* Write a 32-byte segment header into a buffer. */
static void encode_seg_header(uint8_t buf[32], int32_t type, int32_t flags,
                               int64_t content_len, int64_t next_seg_offset,
                               int32_t chunk_count) {
    memset(buf, 0, 32);
    write_le32(buf +  0, (uint32_t)type);
    write_le32(buf +  4, (uint32_t)flags);
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

/* stats_update: retained for potential single-sample use; bulk writers use
   the batch variants below instead. */
#ifdef __GNUC__
__attribute__((unused))
#endif
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
    /* User-defined properties (excluding auto-generated stats) */
    int          property_count;
    ByteBuf      props_blob;
    int          sealed;    /* set once metadata has been written; rejects further property changes */
};

struct MeasGroupWriter {
    char               *name;
    MeasChannelWriter **channels;
    int                 channel_count;
    int                 channel_cap;
    /* Pre-serialised key-value property pairs (excluding the leading count int32) */
    int                 property_count;
    ByteBuf             props_blob;
    int                 sealed; /* set once metadata has been written; rejects further property changes */
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
    MeasCompression    compression;
    int                file_prop_count;
    int                file_prop_cap;
    MeasProperty      *file_props;
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

/* Encode a single property value (key + type + value) into a ByteBuf.
   Used for file-level property encoding. */
static int bbuf_append_property(ByteBuf *b, const MeasProperty *p) {
    if (!bbuf_append_string(b, p->key)) return 0;
    if (!bbuf_append_u8(b, (uint8_t)p->type)) return 0;
    switch (p->type) {
        case MEAS_INT8:
            return bbuf_append_u8(b, (uint8_t)p->value.i8);
        case MEAS_INT16: {
            uint8_t tmp[2]; write_le16(tmp, (uint16_t)p->value.i16);
            return bbuf_append(b, tmp, 2);
        }
        case MEAS_INT32: {
            return bbuf_append_le32(b, (uint32_t)p->value.i32);
        }
        case MEAS_INT64:
        case MEAS_TIMESTAMP:
        case MEAS_TIMESPAN:
            return bbuf_append_le64(b, (uint64_t)p->value.i64);
        case MEAS_UINT8:
            return bbuf_append_u8(b, p->value.u8);
        case MEAS_UINT16: {
            uint8_t tmp[2]; write_le16(tmp, p->value.u16);
            return bbuf_append(b, tmp, 2);
        }
        case MEAS_UINT32:
            return bbuf_append_le32(b, p->value.u32);
        case MEAS_UINT64:
            return bbuf_append_le64(b, p->value.u64);
        case MEAS_FLOAT32: {
            uint8_t tmp[4]; f32_to_le_bytes(tmp, p->value.f32);
            return bbuf_append(b, tmp, 4);
        }
        case MEAS_FLOAT64: {
            uint8_t tmp[8]; f64_to_le_bytes(tmp, p->value.f64);
            return bbuf_append(b, tmp, 8);
        }
        case MEAS_BOOL:
            return bbuf_append_u8(b, p->value.bool_val);
        case MEAS_STRING:
            return bbuf_append_string(b, p->value.str.data ? p->value.str.data : "");
        case MEAS_BINARY:
            if (!bbuf_append_le32(b, (uint32_t)p->value.bin.length)) return 0;
            if (p->value.bin.length > 0)
                return bbuf_append(b, p->value.bin.data, (size_t)p->value.bin.length);
            return 1;
        default:
            return 0;
    }
}

/* Build the metadata segment content for all groups.
   When with_extended is set, prepend [uint8 metaMajor][uint8 metaMinor][file properties]. */
static int build_metadata(const MeasWriter *w, ByteBuf *b, int with_stats,
                           int with_extended, const MeasProperty *file_props,
                           int file_prop_count) {
    if (with_extended) {
        /* Extended metadata version header */
        if (!bbuf_append_u8(b, 0)) return 0;  /* metaMajor */
        if (!bbuf_append_u8(b, 1)) return 0;  /* metaMinor */
        /* File properties: [int32: count][properties...] */
        if (!bbuf_append_le32(b, (uint32_t)file_prop_count)) return 0;
        for (int i = 0; i < file_prop_count; i++) {
            if (!bbuf_append_property(b, &file_props[i])) return 0;
        }
    }

    if (!bbuf_append_le32(b, (uint32_t)w->group_count)) return 0;

    for (int gi = 0; gi < w->group_count; gi++) {
        const MeasGroupWriter *g = w->groups[gi];
        if (!bbuf_append_string(b, g->name)) return 0;
        /* group properties */
        if (!bbuf_append_le32(b, (uint32_t)g->property_count)) return 0;
        if (g->props_blob.size > 0) {
            if (!bbuf_append(b, g->props_blob.data, g->props_blob.size)) return 0;
        }
        if (!bbuf_append_le32(b, (uint32_t)g->channel_count)) return 0;

        for (int ci = 0; ci < g->channel_count; ci++) {
            const MeasChannelWriter *ch = g->channels[ci];
            if (!bbuf_append_string(b, ch->name)) return 0;
            if (!bbuf_append_u8(b, (uint8_t)ch->dtype)) return 0;

            /* channel properties = user props + statistics if applicable */
            if (with_stats && ch->stats.active && ch->stats.count > 0) {
                /* [int32: propCount = user + 8 stats] */
                if (!bbuf_append_le32(b, (uint32_t)(ch->property_count + 8))) return 0;
                if (ch->props_blob.size > 0) {
                    if (!bbuf_append(b, ch->props_blob.data, ch->props_blob.size)) return 0;
                }
                double variance = ch->stats.count > 0
                    ? ch->stats.m2 / (double)ch->stats.count : 0.0;
                /* Pre-computed lengths avoid repeated strlen on constants */
                static const struct { const char *k; size_t klen; MeasDataType t; } keys[] = {
                    {"meas.stats.count",    16, MEAS_INT64},
                    {"meas.stats.min",      14, MEAS_FLOAT64},
                    {"meas.stats.max",      14, MEAS_FLOAT64},
                    {"meas.stats.sum",      14, MEAS_FLOAT64},
                    {"meas.stats.mean",     15, MEAS_FLOAT64},
                    {"meas.stats.variance", 19, MEAS_FLOAT64},
                    {"meas.stats.first",    16, MEAS_FLOAT64},
                    {"meas.stats.last",     15, MEAS_FLOAT64},
                };
                double vals[] = {
                    0, ch->stats.min, ch->stats.max, ch->stats.sum,
                    ch->stats.mean, variance, ch->stats.first, ch->stats.last
                };
                for (int k = 0; k < 8; k++) {
                    if (!bbuf_append_string_n(b, keys[k].k, keys[k].klen)) return 0;
                    if (keys[k].t == MEAS_INT64) {
                        if (!bbuf_append_u8(b, (uint8_t)MEAS_INT64)) return 0;
                        if (!bbuf_append_le64(b, (uint64_t)ch->stats.count)) return 0;
                    } else {
                        if (!bbuf_append_u8(b, (uint8_t)MEAS_FLOAT64)) return 0;
                        uint8_t vb[8]; f64_to_le_bytes(vb, vals[k]);
                        if (!bbuf_append(b, vb, 8)) return 0;
                    }
                }
            } else if (with_stats && ch->stats.active) {
                /* Write user props + placeholder zeroed stats (same byte count) */
                if (!bbuf_append_le32(b, (uint32_t)(ch->property_count + 8))) return 0;
                if (ch->props_blob.size > 0) {
                    if (!bbuf_append(b, ch->props_blob.data, ch->props_blob.size)) return 0;
                }
                static const struct { const char *k; size_t klen; MeasDataType t; } keys[] = {
                    {"meas.stats.count",    16, MEAS_INT64},
                    {"meas.stats.min",      14, MEAS_FLOAT64},
                    {"meas.stats.max",      14, MEAS_FLOAT64},
                    {"meas.stats.sum",      14, MEAS_FLOAT64},
                    {"meas.stats.mean",     15, MEAS_FLOAT64},
                    {"meas.stats.variance", 19, MEAS_FLOAT64},
                    {"meas.stats.first",    16, MEAS_FLOAT64},
                    {"meas.stats.last",     15, MEAS_FLOAT64},
                };
                for (int k = 0; k < 8; k++) {
                    if (!bbuf_append_string_n(b, keys[k].k, keys[k].klen)) return 0;
                    if (keys[k].t == MEAS_INT64) {
                        if (!bbuf_append_u8(b, (uint8_t)MEAS_INT64)) return 0;
                        if (!bbuf_append_le64(b, 0)) return 0;
                    } else {
                        if (!bbuf_append_u8(b, (uint8_t)MEAS_FLOAT64)) return 0;
                        uint8_t z[8] = {0}; if (!bbuf_append(b, z, 8)) return 0;
                    }
                }
            } else {
                /* user properties only (no stats) */
                if (!bbuf_append_le32(b, (uint32_t)ch->property_count)) return 0;
                if (ch->props_blob.size > 0) {
                    if (!bbuf_append(b, ch->props_blob.data, ch->props_blob.size)) return 0;
                }
            }
        }
    }
    return 1;
}

/* Write a segment (header + content) at the current file position.
   Patches NextSegmentOffset in the header.
   Increments w->segment_count.
   Returns 0 on error. */
static int write_segment(MeasWriter *w, int32_t type, int32_t flags,
                          const uint8_t *content, size_t content_len,
                          int32_t chunk_count) {
    long seg_start = ftell(w->file);
    if (seg_start < 0) return 0;

    /* Pre-compute NextSegmentOffset: header(32) + content = next segment */
    int64_t next_off = (int64_t)seg_start + MEAS_SEG_HEADER_SIZE + (int64_t)content_len;

    uint8_t hdr[32];
    encode_seg_header(hdr, type, flags, (int64_t)content_len, next_off, chunk_count);
    if (fwrite(hdr, 1, 32, w->file) != 32) return 0;
    if (content_len > 0) {
        if (fwrite(content, 1, content_len, w->file) != content_len) return 0;
    }

    w->segment_count++;
    return 1;
}

static int ensure_metadata(MeasWriter *w) {
    if (w->metadata_written) return 1;
    w->metadata_written = 1;

    /* Seal all groups and channels so property setters are rejected */
    for (int gi = 0; gi < w->group_count; gi++) {
        w->groups[gi]->sealed = 1;
        for (int ci = 0; ci < w->groups[gi]->channel_count; ci++)
            w->groups[gi]->channels[ci]->sealed = 1;
    }

    /* Assign global channel indices */
    int idx = 0;
    for (int gi = 0; gi < w->group_count; gi++)
        for (int ci = 0; ci < w->groups[gi]->channel_count; ci++)
            w->groups[gi]->channels[ci]->global_index = idx++;

    /* Write actual file header with MEAS_FLAG_EXTENDED_METADATA set */
    uint8_t fhdr[64];
    encode_file_header(fhdr, w->created_at_ns, 0, w->file_id,
                       MEAS_FLAG_EXTENDED_METADATA);
    if (fseek(w->file, 0, SEEK_SET) != 0) return 0;
    if (fwrite(fhdr, 1, 64, w->file) != 64) return 0;
    if (fseek(w->file, 0, SEEK_END) != 0) return 0;

    /* Build and write metadata segment with placeholder stats */
    ByteBuf meta; bbuf_init(&meta);
    if (!build_metadata(w, &meta, 1 /* with placeholder stats */,
                        1, w->file_props, w->file_prop_count)) {
        bbuf_free(&meta); return 0;
    }
    w->metadata_content_offset = ftell(w->file) + MEAS_SEG_HEADER_SIZE;
    int ok = write_segment(w, MEAS_SEG_TYPE_METADATA, 0, meta.data, meta.size, 0);
    bbuf_free(&meta);
    return ok;
}

/* ── Writer public API ────────────────────────────────────────────────────── */

MeasWriter *meas_writer_open(const char *path) {
    if (!path) return NULL;
    FILE *f = fopen(path, "wb");
    if (!f) return NULL;

    MeasWriter *w = (MeasWriter *)calloc(1, sizeof(MeasWriter));
    if (!w) { fclose(f); return NULL; }

    w->file = f;
    w->created_at_ns = now_nanos();
    gen_uuid(w->file_id);

    /* Use 64 KB stdio buffer — reduces write syscalls for large data */
    setvbuf(f, NULL, _IOFBF, 65536);

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
    bbuf_init(&ch->props_blob);

    group->channels[group->channel_count++] = ch;
    return ch;
}

void meas_channel_set_statistics(MeasChannelWriter *ch, int enable) {
    if (!ch) return;
    ch->stats.active = enable ? dtype_supports_stats(ch->dtype) : 0;
}

int meas_writer_set_compression(MeasWriter *writer, MeasCompression compression) {
    if (!writer || writer->metadata_written) return -1;
    switch (compression) {
        case MEAS_COMPRESS_NONE: break;
#ifdef MEAS_HAVE_LZ4
        case MEAS_COMPRESS_LZ4: break;
#endif
#ifdef MEAS_HAVE_ZSTD
        case MEAS_COMPRESS_ZSTD: break;
#endif
        default: return -1; /* unsupported */
    }
    writer->compression = compression;
    return 0;
}

/* ── File-level property writer helpers ────────────────────────────────── */

/* Grow the file_props array if needed and return a pointer to the next slot */
static MeasProperty *writer_alloc_file_prop(MeasWriter *w) {
    if (w->file_prop_count == w->file_prop_cap) {
        int cap = w->file_prop_cap ? w->file_prop_cap * 2 : 4;
        MeasProperty *p = (MeasProperty *)realloc(
            w->file_props, (size_t)cap * sizeof(MeasProperty));
        if (!p) return NULL;
        w->file_props = p;
        w->file_prop_cap = cap;
    }
    MeasProperty *prop = &w->file_props[w->file_prop_count];
    memset(prop, 0, sizeof(*prop));
    return prop;
}

int meas_writer_set_property_str(MeasWriter *writer, const char *key, const char *value) {
    if (!writer || !key || !value || writer->metadata_written) return -1;
    MeasProperty *p = writer_alloc_file_prop(writer);
    if (!p) return -1;
    p->key = strdup(key);
    if (!p->key) return -1;
    p->type = MEAS_STRING;
    p->value.str.data = strdup(value);
    if (!p->value.str.data) { free(p->key); p->key = NULL; return -1; }
    p->value.str.length = (int32_t)strlen(value);
    writer->file_prop_count++;
    return 0;
}

int meas_writer_set_property_i32(MeasWriter *writer, const char *key, int32_t value) {
    if (!writer || !key || writer->metadata_written) return -1;
    MeasProperty *p = writer_alloc_file_prop(writer);
    if (!p) return -1;
    p->key = strdup(key);
    if (!p->key) return -1;
    p->type = MEAS_INT32;
    p->value.i32 = value;
    writer->file_prop_count++;
    return 0;
}

int meas_writer_set_property_f64(MeasWriter *writer, const char *key, double value) {
    if (!writer || !key || writer->metadata_written) return -1;
    MeasProperty *p = writer_alloc_file_prop(writer);
    if (!p) return -1;
    p->key = strdup(key);
    if (!p->key) return -1;
    p->type = MEAS_FLOAT64;
    p->value.f64 = value;
    writer->file_prop_count++;
    return 0;
}

/* Compress data in-place into a new buffer; returns NULL on failure.
   Caller must free() the returned pointer.
   *out_len is set to the compressed size. */
static uint8_t *compress_segment(const uint8_t *data, size_t len,
                                  MeasCompression algo, size_t *out_len) {
    (void)data; (void)len; (void)algo; (void)out_len;
#ifdef MEAS_HAVE_LZ4
    if (algo == MEAS_COMPRESS_LZ4) {
        int max_dst = LZ4_compressBound((int)len);
        uint8_t *buf = (uint8_t *)malloc(4 + (size_t)max_dst);
        if (!buf) return NULL;
        /* 4-byte LE original size prefix (same as C# impl) */
        write_le32(buf, (uint32_t)len);
        int compressed = LZ4_compress_default((const char *)data, (char *)(buf + 4),
                                               (int)len, max_dst);
        if (compressed <= 0) { free(buf); return NULL; }
        *out_len = 4 + (size_t)compressed;
        return buf;
    }
#endif
#ifdef MEAS_HAVE_ZSTD
    if (algo == MEAS_COMPRESS_ZSTD) {
        size_t bound = ZSTD_compressBound(len);
        uint8_t *buf = (uint8_t *)malloc(bound);
        if (!buf) return NULL;
        size_t compressed = ZSTD_compress(buf, bound, data, len, 3);
        if (ZSTD_isError(compressed)) { free(buf); return NULL; }
        *out_len = compressed;
        return buf;
    }
#endif
    return NULL;
}

/* Decompress segment content; returns NULL on failure.
   Caller must free() the returned pointer.
   *out_len is set to the decompressed size. */
static uint8_t *decompress_segment(const uint8_t *data, size_t len,
                                    MeasCompression algo, size_t *out_len) {
    (void)data; (void)len; (void)algo; (void)out_len;
#ifdef MEAS_HAVE_LZ4
    if (algo == MEAS_COMPRESS_LZ4) {
        if (len < 4) return NULL;
        uint32_t orig_size = read_le32(data);
        uint8_t *buf = (uint8_t *)malloc(orig_size);
        if (!buf) return NULL;
        int decoded = LZ4_decompress_safe((const char *)(data + 4), (char *)buf,
                                           (int)(len - 4), (int)orig_size);
        if (decoded < 0) { free(buf); return NULL; }
        *out_len = (size_t)orig_size;
        return buf;
    }
#endif
#ifdef MEAS_HAVE_ZSTD
    if (algo == MEAS_COMPRESS_ZSTD) {
        unsigned long long orig_size = ZSTD_getFrameContentSize(data, len);
        if (orig_size == ZSTD_CONTENTSIZE_ERROR ||
            orig_size == ZSTD_CONTENTSIZE_UNKNOWN) return NULL;
        uint8_t *buf = (uint8_t *)malloc((size_t)orig_size);
        if (!buf) return NULL;
        size_t decoded = ZSTD_decompress(buf, (size_t)orig_size, data, len);
        if (ZSTD_isError(decoded)) { free(buf); return NULL; }
        *out_len = decoded;
        return buf;
    }
#endif
    return NULL;
}

/* Compute total data segment content size for pending channels */
static size_t compute_data_content_size(const MeasWriter *w) {
    size_t total = 4; /* int32: chunkCount */
    for (int gi = 0; gi < w->group_count; gi++)
        for (int ci = 0; ci < w->groups[gi]->channel_count; ci++) {
            MeasChannelWriter *ch = w->groups[gi]->channels[ci];
            if (ch->buf.size == 0) continue;
            total += 4 + 8 + 8 + ch->buf.size; /* index + sampleCount + dataByteLen + data */
        }
    return total;
}

/* Internal flush — skip_fflush avoids redundant fflush when called from close
   (fclose already flushes). */
static int flush_internal(MeasWriter *writer, int skip_fflush) {
    if (!writer) return -1;
    if (!ensure_metadata(writer)) return -1;

    /* Count pending channels */
    int pending = 0;
    for (int gi = 0; gi < writer->group_count; gi++)
        for (int ci = 0; ci < writer->groups[gi]->channel_count; ci++)
            if (writer->groups[gi]->channels[ci]->buf.size > 0) pending++;
    if (pending == 0) return 0;

    /* Uncompressed fast path: write segment header + data directly to file
       without copying all channel data into an intermediate buffer. */
    if (writer->compression == MEAS_COMPRESS_NONE) {
        size_t content_len = compute_data_content_size(writer);
        long seg_start = ftell(writer->file);
        if (seg_start < 0) return -1;
        int64_t next_off = (int64_t)seg_start + MEAS_SEG_HEADER_SIZE + (int64_t)content_len;

        uint8_t hdr[32];
        encode_seg_header(hdr, MEAS_SEG_TYPE_DATA, 0, (int64_t)content_len, next_off, pending);
        if (fwrite(hdr, 1, 32, writer->file) != 32) return -1;

        /* Write chunkCount */
        uint8_t tmp[20]; /* big enough for int32 + int64 + int64 */
        write_le32(tmp, (uint32_t)pending);
        if (fwrite(tmp, 1, 4, writer->file) != 4) return -1;

        /* Write each pending channel directly */
        for (int gi = 0; gi < writer->group_count; gi++) {
            for (int ci = 0; ci < writer->groups[gi]->channel_count; ci++) {
                MeasChannelWriter *ch = writer->groups[gi]->channels[ci];
                if (ch->buf.size == 0) continue;
                /* Chunk header: index(4) + sampleCount(8) + dataByteLen(8) */
                write_le32(tmp, (uint32_t)ch->global_index);
                write_le64(tmp + 4, (uint64_t)ch->sample_count_pending);
                write_le64(tmp + 12, (uint64_t)ch->buf.size);
                if (fwrite(tmp, 1, 20, writer->file) != 20) return -1;
                /* Write channel data directly — no intermediate copy */
                if (fwrite(ch->buf.data, 1, ch->buf.size, writer->file) != ch->buf.size) return -1;
                ch->buf.size = 0;
                ch->sample_count_pending = 0;
            }
        }
        writer->segment_count++;
        if (!skip_fflush && fflush(writer->file) != 0) return -1;
        return 0;
    }

    /* Compressed path: must build segment content in memory for compression */
    ByteBuf seg; bbuf_init(&seg);
    if (!bbuf_append_le32(&seg, (uint32_t)pending)) { bbuf_free(&seg); return -1; }

    for (int gi = 0; gi < writer->group_count; gi++) {
        for (int ci = 0; ci < writer->groups[gi]->channel_count; ci++) {
            MeasChannelWriter *ch = writer->groups[gi]->channels[ci];
            if (ch->buf.size == 0) continue;
            if (!bbuf_append_le32(&seg, (uint32_t)ch->global_index)) goto fail;
            if (!bbuf_append_le64(&seg, (uint64_t)ch->sample_count_pending)) goto fail;
            if (!bbuf_append_le64(&seg, (uint64_t)ch->buf.size)) goto fail;
            if (!bbuf_append(&seg, ch->buf.data, ch->buf.size)) goto fail;
            ch->buf.size = 0;
            ch->sample_count_pending = 0;
        }
    }

    size_t comp_len = 0;
    uint8_t *compressed = compress_segment(seg.data, seg.size, writer->compression, &comp_len);
    if (!compressed) { bbuf_free(&seg); return -1; }

    int32_t flags = (int32_t)(writer->compression & 0x0F);
    int ok = write_segment(writer, MEAS_SEG_TYPE_DATA, flags,
                            compressed, comp_len, pending);
    free(compressed);
    bbuf_free(&seg);
    if (!skip_fflush && fflush(writer->file) != 0) return -1;
    return ok ? 0 : -1;

fail:
    bbuf_free(&seg); return -1;
}

int meas_writer_flush(MeasWriter *writer) {
    return flush_internal(writer, 0);
}

void meas_writer_close(MeasWriter *writer) {
    if (!writer) return;

    /* Flush any remaining data — skip fflush since fclose will flush */
    flush_internal(writer, 1);

    /* Patch metadata segment in-place with final statistics.
       Skip entirely when no channel has active stats — the metadata
       is identical to what was written at first flush time. */
    if (writer->metadata_written && writer->metadata_content_offset > 0) {
        int any_stats = 0;
        for (int gi = 0; gi < writer->group_count && !any_stats; gi++)
            for (int ci = 0; ci < writer->groups[gi]->channel_count && !any_stats; ci++)
                if (writer->groups[gi]->channels[ci]->stats.active)
                    any_stats = 1;
        if (any_stats) {
            ByteBuf final_meta; bbuf_init(&final_meta);
            if (build_metadata(writer, &final_meta, 1 /* with real stats */,
                               1, writer->file_props, writer->file_prop_count)) {
                fseek(writer->file, (long)writer->metadata_content_offset, SEEK_SET);
                fwrite(final_meta.data, 1, final_meta.size, writer->file);
                fseek(writer->file, 0, SEEK_END);
            }
            bbuf_free(&final_meta);
        }
    }

    /* Patch file header with final segment count */
    uint8_t fhdr[64];
    encode_file_header(fhdr, writer->created_at_ns, writer->segment_count, writer->file_id,
                       MEAS_FLAG_EXTENDED_METADATA);
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
            bbuf_free(&ch->props_blob);
            free(ch);
        }
        free(g->channels);
        free(g->name);
        bbuf_free(&g->props_blob);
        free(g);
    }
    free(writer->groups);
    free_properties(writer->file_props, writer->file_prop_count);
    free(writer);
}

/* ── Endianness detection ─────────────────────────────────────────────────── */

#if defined(__BYTE_ORDER__) && __BYTE_ORDER__ == __ORDER_LITTLE_ENDIAN__
#define MEAS_IS_LE 1
#elif defined(_WIN32) || defined(__x86_64__) || defined(__i386__) || \
      defined(__aarch64__) || defined(__arm__) || defined(_M_AMD64) || \
      defined(_M_IX86) || defined(_M_ARM64)
#define MEAS_IS_LE 1
#else
#define MEAS_IS_LE 0
#endif

/* ── Batch statistics helpers ────────────────────────────────────────────── */

/* Batch stats for float32 arrays: single-pass min/max/sum + Welford M2.
   Computes all statistics in one traversal of the data. */
static void stats_update_f32_bulk(StatsAcc *s, const float *data, int64_t count) {
    if (!s->active || count == 0) return;
    double batch_min = (double)data[0], batch_max = batch_min;
    double batch_sum = 0.0;
    double batch_mean = 0.0, batch_m2 = 0.0;
    for (int64_t i = 0; i < count; i++) {
        double v = (double)data[i];
        if (v < batch_min) batch_min = v;
        if (v > batch_max) batch_max = v;
        batch_sum += v;
        /* Online Welford within batch */
        double d1 = v - batch_mean;
        batch_mean += d1 / (double)(i + 1);
        double d2 = v - batch_mean;
        batch_m2 += d1 * d2;
    }
    if (s->count == 0) {
        s->first = (double)data[0];
        s->min = batch_min;
        s->max = batch_max;
    } else {
        if (batch_min < s->min) s->min = batch_min;
        if (batch_max > s->max) s->max = batch_max;
    }
    s->last = (double)data[count - 1];
    int64_t old_count = s->count;
    int64_t new_count = old_count + count;
    double delta = batch_mean - s->mean;
    s->m2 += batch_m2 + delta * delta * (double)old_count * (double)count / (double)new_count;
    s->mean += delta * (double)count / (double)new_count;
    s->sum += batch_sum;
    s->count = new_count;
}

static void stats_update_f64_bulk(StatsAcc *s, const double *data, int64_t count) {
    if (!s->active || count == 0) return;
    double batch_min = data[0], batch_max = data[0];
    double batch_sum = 0.0;
    double batch_mean = 0.0, batch_m2 = 0.0;
    for (int64_t i = 0; i < count; i++) {
        double v = data[i];
        if (v < batch_min) batch_min = v;
        if (v > batch_max) batch_max = v;
        batch_sum += v;
        double d1 = v - batch_mean;
        batch_mean += d1 / (double)(i + 1);
        double d2 = v - batch_mean;
        batch_m2 += d1 * d2;
    }
    if (s->count == 0) {
        s->first = data[0];
        s->min = batch_min;
        s->max = batch_max;
    } else {
        if (batch_min < s->min) s->min = batch_min;
        if (batch_max > s->max) s->max = batch_max;
    }
    s->last = data[count - 1];
    int64_t old_count = s->count;
    int64_t new_count = old_count + count;
    double delta = batch_mean - s->mean;
    s->m2 += batch_m2 + delta * delta * (double)old_count * (double)count / (double)new_count;
    s->mean += delta * (double)count / (double)new_count;
    s->sum += batch_sum;
    s->count = new_count;
}

/* Generic batch stats for integer types (converted to double).
   Single-pass: min/max/sum + online Welford M2. */
#define DEFINE_STATS_BULK_INT(SUFFIX, CTYPE)                                   \
static void stats_update_##SUFFIX##_bulk(StatsAcc *s, const CTYPE *data,       \
                                          int64_t count) {                     \
    if (!s->active || count == 0) return;                                      \
    double batch_min = (double)data[0], batch_max = batch_min;                 \
    double batch_sum = 0.0;                                                    \
    double batch_mean = 0.0, batch_m2 = 0.0;                                  \
    for (int64_t i = 0; i < count; i++) {                                      \
        double v = (double)data[i];                                            \
        if (v < batch_min) batch_min = v;                                      \
        if (v > batch_max) batch_max = v;                                      \
        batch_sum += v;                                                        \
        double d1 = v - batch_mean;                                            \
        batch_mean += d1 / (double)(i + 1);                                    \
        double d2 = v - batch_mean;                                            \
        batch_m2 += d1 * d2;                                                   \
    }                                                                          \
    if (s->count == 0) {                                                       \
        s->first = (double)data[0]; s->min = batch_min; s->max = batch_max;   \
    } else {                                                                   \
        if (batch_min < s->min) s->min = batch_min;                            \
        if (batch_max > s->max) s->max = batch_max;                            \
    }                                                                          \
    s->last = (double)data[count - 1];                                         \
    int64_t old_count = s->count, new_count = old_count + count;               \
    double delta = batch_mean - s->mean;                                       \
    s->m2 += batch_m2 + delta * delta * (double)old_count * (double)count      \
             / (double)new_count;                                              \
    s->mean += delta * (double)count / (double)new_count;                      \
    s->sum += batch_sum;                                                       \
    s->count = new_count;                                                      \
}
DEFINE_STATS_BULK_INT(i8,  int8_t)
DEFINE_STATS_BULK_INT(i16, int16_t)
DEFINE_STATS_BULK_INT(i32, int32_t)
DEFINE_STATS_BULK_INT(i64, int64_t)
DEFINE_STATS_BULK_INT(u8,  uint8_t)
DEFINE_STATS_BULK_INT(u16, uint16_t)
DEFINE_STATS_BULK_INT(u32, uint32_t)
DEFINE_STATS_BULK_INT(u64, uint64_t)

/* ── Typed write helpers ──────────────────────────────────────────────────── */

/* On little-endian systems (x86, ARM, etc.) data can be memcpy'd directly.
   Statistics are computed in a separate batch pass (parallel Welford)
   instead of per-element division. */

/* Helper: bulk-copy data into buffer (LE fast path or per-element encoding) */
#if MEAS_IS_LE
#define BULK_COPY(ch, src_ptr, cnt, elem_size)                                 \
    do {                                                                       \
        size_t _bc = (size_t)(cnt) * (elem_size);                              \
        if (!bbuf_reserve(&(ch)->buf, _bc)) return -1;                         \
        memcpy((ch)->buf.data + (ch)->buf.size, (src_ptr), _bc);               \
        (ch)->buf.size += _bc;                                                 \
    } while (0)
#else
#define BULK_COPY(ch, src_ptr, cnt, elem_size) ((void)0) /* handled per type */
#endif

int meas_channel_write_f32(MeasChannelWriter *ch, const float *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_FLOAT32) return -1;
#if MEAS_IS_LE
    BULK_COPY(ch, data, count, 4);
#else
    if (!bbuf_reserve(&ch->buf, (size_t)(count * 4))) return -1;
    for (int64_t i = 0; i < count; i++) {
        f32_to_le_bytes(ch->buf.data + ch->buf.size, data[i]);
        ch->buf.size += 4;
    }
#endif
    stats_update_f32_bulk(&ch->stats, data, count);
    ch->sample_count_pending += count;
    return 0;
}
int meas_channel_write_f64(MeasChannelWriter *ch, const double *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_FLOAT64) return -1;
#if MEAS_IS_LE
    BULK_COPY(ch, data, count, 8);
#else
    if (!bbuf_reserve(&ch->buf, (size_t)(count * 8))) return -1;
    for (int64_t i = 0; i < count; i++) {
        f64_to_le_bytes(ch->buf.data + ch->buf.size, data[i]);
        ch->buf.size += 8;
    }
#endif
    stats_update_f64_bulk(&ch->stats, data, count);
    ch->sample_count_pending += count;
    return 0;
}
int meas_channel_write_i8(MeasChannelWriter *ch, const int8_t *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_INT8) return -1;
    if (!bbuf_append(&ch->buf, data, (size_t)count)) return -1;
    stats_update_i8_bulk(&ch->stats, data, count);
    ch->sample_count_pending += count; return 0;
}
int meas_channel_write_i16(MeasChannelWriter *ch, const int16_t *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_INT16) return -1;
#if MEAS_IS_LE
    BULK_COPY(ch, data, count, 2);
#else
    if (!bbuf_reserve(&ch->buf, (size_t)(count * 2))) return -1;
    for (int64_t i = 0; i < count; i++) {
        write_le16(ch->buf.data + ch->buf.size, (uint16_t)data[i]);
        ch->buf.size += 2;
    }
#endif
    stats_update_i16_bulk(&ch->stats, data, count);
    ch->sample_count_pending += count; return 0;
}
int meas_channel_write_i32(MeasChannelWriter *ch, const int32_t *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_INT32) return -1;
#if MEAS_IS_LE
    BULK_COPY(ch, data, count, 4);
#else
    if (!bbuf_reserve(&ch->buf, (size_t)(count * 4))) return -1;
    for (int64_t i = 0; i < count; i++) {
        write_le32(ch->buf.data + ch->buf.size, (uint32_t)data[i]);
        ch->buf.size += 4;
    }
#endif
    stats_update_i32_bulk(&ch->stats, data, count);
    ch->sample_count_pending += count; return 0;
}
int meas_channel_write_i64(MeasChannelWriter *ch, const int64_t *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_INT64) return -1;
#if MEAS_IS_LE
    BULK_COPY(ch, data, count, 8);
#else
    if (!bbuf_reserve(&ch->buf, (size_t)(count * 8))) return -1;
    for (int64_t i = 0; i < count; i++) {
        write_le64(ch->buf.data + ch->buf.size, (uint64_t)data[i]);
        ch->buf.size += 8;
    }
#endif
    stats_update_i64_bulk(&ch->stats, data, count);
    ch->sample_count_pending += count; return 0;
}
int meas_channel_write_u8(MeasChannelWriter *ch, const uint8_t *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_UINT8) return -1;
    if (!bbuf_append(&ch->buf, data, (size_t)count)) return -1;
    stats_update_u8_bulk(&ch->stats, data, count);
    ch->sample_count_pending += count; return 0;
}
int meas_channel_write_u16(MeasChannelWriter *ch, const uint16_t *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_UINT16) return -1;
#if MEAS_IS_LE
    BULK_COPY(ch, data, count, 2);
#else
    if (!bbuf_reserve(&ch->buf, (size_t)(count * 2))) return -1;
    for (int64_t i = 0; i < count; i++) {
        write_le16(ch->buf.data + ch->buf.size, data[i]);
        ch->buf.size += 2;
    }
#endif
    stats_update_u16_bulk(&ch->stats, data, count);
    ch->sample_count_pending += count; return 0;
}
int meas_channel_write_u32(MeasChannelWriter *ch, const uint32_t *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_UINT32) return -1;
#if MEAS_IS_LE
    BULK_COPY(ch, data, count, 4);
#else
    if (!bbuf_reserve(&ch->buf, (size_t)(count * 4))) return -1;
    for (int64_t i = 0; i < count; i++) {
        write_le32(ch->buf.data + ch->buf.size, data[i]);
        ch->buf.size += 4;
    }
#endif
    stats_update_u32_bulk(&ch->stats, data, count);
    ch->sample_count_pending += count; return 0;
}
int meas_channel_write_u64(MeasChannelWriter *ch, const uint64_t *data, int64_t count) {
    if (!ch || ch->dtype != MEAS_UINT64) return -1;
#if MEAS_IS_LE
    BULK_COPY(ch, data, count, 8);
#else
    if (!bbuf_reserve(&ch->buf, (size_t)(count * 8))) return -1;
    for (int64_t i = 0; i < count; i++) {
        write_le64(ch->buf.data + ch->buf.size, data[i]);
        ch->buf.size += 8;
    }
#endif
    stats_update_u64_bulk(&ch->stats, data, count);
    ch->sample_count_pending += count; return 0;
}
int meas_channel_write_timestamp(MeasChannelWriter *ch, const int64_t *ns, int64_t count) {
    if (!ch || (ch->dtype != MEAS_TIMESTAMP && ch->dtype != MEAS_TIMESPAN)) return -1;
#if MEAS_IS_LE
    BULK_COPY(ch, ns, count, 8);
#else
    if (!bbuf_reserve(&ch->buf, (size_t)(count * 8))) return -1;
    for (int64_t i = 0; i < count; i++) {
        write_le64(ch->buf.data + ch->buf.size, (uint64_t)ns[i]);
        ch->buf.size += 8;
    }
#endif
    ch->sample_count_pending += count; return 0;
}
int meas_channel_write_frame(MeasChannelWriter *ch, const uint8_t *frame, int32_t length) {
    if (!ch || ch->dtype != MEAS_BINARY) return -1;
    uint8_t len_buf[4]; write_le32(len_buf, (uint32_t)length);
    if (!bbuf_append(&ch->buf, len_buf, 4)) return -1;
    if (length > 0 && !bbuf_append(&ch->buf, frame, (size_t)length)) return -1;
    ch->sample_count_pending++; return 0;
}

/* Write a single UTF-8 string sample (§7: same frame format as MEAS_BINARY).
   Stores [int32: byteLength][UTF-8 bytes] without a null terminator.
   Rejects strings longer than INT32_MAX bytes. */
int meas_channel_write_string(MeasChannelWriter *ch, const char *str) {
    if (!ch || ch->dtype != MEAS_STRING || !str) return -1;
    size_t slen = strlen(str);
    if (slen > (size_t)INT32_MAX) return -1;  /* guard against overflow */
    int32_t len = (int32_t)slen;
    uint8_t len_buf[4]; write_le32(len_buf, (uint32_t)len);
    if (!bbuf_append(&ch->buf, len_buf, 4)) return -1;
    if (len > 0 && !bbuf_append(&ch->buf, str, (size_t)len)) return -1;
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

/* ── §10 Bus Metadata encode / decode ───────────────────────────────────── */

/* Forward declarations for mutual recursion */
static int encode_signal_definition(ByteBuf *b, const MeasSignalDefinition *sig);
static int encode_pdu_definition   (ByteBuf *b, const MeasPduDefinition    *pdu);

/* §10.4 MultiplexCondition */
static int encode_multiplex_condition(ByteBuf *b, const MeasMultiplexCondition *mc) {
    if (!bbuf_append_string(b, mc->multiplexer_signal_name)) return 0;
    if (!bbuf_append_le64(b, (uint64_t)mc->low_value))      return 0;
    if (!bbuf_append_le64(b, (uint64_t)mc->high_value))     return 0;
    if (!bbuf_append_u8  (b, mc->has_parent ? 1 : 0))       return 0;
    if (mc->has_parent && mc->parent)
        if (!encode_multiplex_condition(b, mc->parent))      return 0;
    return 1;
}

/* §10.3 SignalDefinition */
static int encode_signal_definition(ByteBuf *b, const MeasSignalDefinition *sig) {
    if (!bbuf_append_string(b, sig->name))                            return 0;
    if (!bbuf_append_le32(b, (uint32_t)sig->start_bit))               return 0;
    if (!bbuf_append_le32(b, (uint32_t)sig->bit_length))              return 0;
    if (!bbuf_append_u8  (b, sig->byte_order))                        return 0;
    if (!bbuf_append_u8  (b, sig->signal_type))                       return 0;
    uint8_t fv[8];
    f64_to_le_bytes(fv, sig->factor); if (!bbuf_append(b, fv, 8))    return 0;
    f64_to_le_bytes(fv, sig->offset); if (!bbuf_append(b, fv, 8))    return 0;
    if (!bbuf_append_u8(b, sig->min_max_flags))                       return 0;
    if (sig->min_max_flags & 0x01) {
        f64_to_le_bytes(fv, sig->min_value); if (!bbuf_append(b, fv, 8)) return 0;
    }
    if (sig->min_max_flags & 0x02) {
        f64_to_le_bytes(fv, sig->max_value); if (!bbuf_append(b, fv, 8)) return 0;
    }
    if (!bbuf_append_u8(b, sig->has_unit ? 1 : 0)) return 0;
    if (sig->has_unit && sig->unit)
        if (!bbuf_append_string(b, sig->unit)) return 0;
    if (!bbuf_append_u8(b, sig->is_multiplexer         ? 1 : 0)) return 0;
    if (!bbuf_append_u8(b, sig->has_multiplex_condition ? 1 : 0)) return 0;
    if (sig->has_multiplex_condition && sig->multiplex_condition)
        if (!encode_multiplex_condition(b, sig->multiplex_condition)) return 0;
    if (!bbuf_append_le32(b, (uint32_t)sig->value_desc_count)) return 0;
    for (int32_t i = 0; i < sig->value_desc_count; i++) {
        if (!bbuf_append_le64(b, (uint64_t)sig->value_descs[i].value)) return 0;
        if (!bbuf_append_string(b, sig->value_descs[i].description))   return 0;
    }
    return 1;
}

/* §10.6 E2EProtection */
static int encode_e2e_protection(ByteBuf *b, const MeasE2EProtection *e) {
    if (!bbuf_append_u8  (b, e->profile))              return 0;
    if (!bbuf_append_le32(b, (uint32_t)e->crc_start_bit))      return 0;
    if (!bbuf_append_le32(b, (uint32_t)e->crc_bit_length))     return 0;
    if (!bbuf_append_le32(b, (uint32_t)e->counter_start_bit))  return 0;
    if (!bbuf_append_le32(b, (uint32_t)e->counter_bit_length)) return 0;
    if (!bbuf_append_le32(b, e->data_id))              return 0;
    if (!bbuf_append_le32(b, e->crc_polynomial))       return 0;
    return 1;
}

/* §10.7 SecOcConfig */
static int encode_secoc_config(ByteBuf *b, const MeasSecOcConfig *s) {
    if (!bbuf_append_u8  (b, s->algorithm))                              return 0;
    if (!bbuf_append_le32(b, (uint32_t)s->freshness_start_bit))          return 0;
    if (!bbuf_append_le32(b, (uint32_t)s->freshness_truncated_length))   return 0;
    if (!bbuf_append_le32(b, (uint32_t)s->freshness_full_length))        return 0;
    if (!bbuf_append_u8  (b, s->freshness_type))                         return 0;
    if (!bbuf_append_le32(b, (uint32_t)s->mac_start_bit))                return 0;
    if (!bbuf_append_le32(b, (uint32_t)s->mac_truncated_length))         return 0;
    if (!bbuf_append_le32(b, (uint32_t)s->mac_full_length))              return 0;
    if (!bbuf_append_le32(b, (uint32_t)s->authen_payload_length))        return 0;
    if (!bbuf_append_le32(b, s->data_id))                                return 0;
    if (!bbuf_append_le32(b, (uint32_t)s->auth_build_attempts))          return 0;
    if (!bbuf_append_u8  (b, s->use_freshness_value_manager ? 1 : 0))   return 0;
    if (!bbuf_append_le32(b, s->key_id))                                 return 0;
    return 1;
}

/* §10.8 MultiplexConfig */
static int encode_multiplex_config(ByteBuf *b, const MeasMultiplexConfig *mx) {
    if (!bbuf_append_string(b, mx->multiplexer_signal_name)) return 0;
    if (!bbuf_append_le32  (b, (uint32_t)mx->group_count))  return 0;
    for (int32_t i = 0; i < mx->group_count; i++) {
        const MeasMuxGroup *grp = &mx->groups[i];
        if (!bbuf_append_le64(b, (uint64_t)grp->mux_value))          return 0;
        if (!bbuf_append_le32(b, (uint32_t)grp->signal_name_count))  return 0;
        for (int32_t j = 0; j < grp->signal_name_count; j++)
            if (!bbuf_append_string(b, grp->signal_names[j]))        return 0;
    }
    return 1;
}

/* §10.9 ContainedPdu */
static int encode_contained_pdu(ByteBuf *b, const MeasContainedPdu *cpdu) {
    if (!bbuf_append_string(b, cpdu->name))                 return 0;
    if (!bbuf_append_le32  (b, cpdu->header_id))            return 0;
    if (!bbuf_append_le32  (b, (uint32_t)cpdu->length))     return 0;
    if (!bbuf_append_le32  (b, (uint32_t)cpdu->signal_count)) return 0;
    for (int32_t i = 0; i < cpdu->signal_count; i++)
        if (!encode_signal_definition(b, &cpdu->signals[i])) return 0;
    return 1;
}

/* §10.5 PduDefinition */
static int encode_pdu_definition(ByteBuf *b, const MeasPduDefinition *pdu) {
    if (!bbuf_append_string(b, pdu->name))                    return 0;
    if (!bbuf_append_le32  (b, pdu->pdu_id))                  return 0;
    if (!bbuf_append_le32  (b, (uint32_t)pdu->byte_offset))   return 0;
    if (!bbuf_append_le32  (b, (uint32_t)pdu->length))        return 0;
    if (!bbuf_append_u8    (b, pdu->is_container_pdu ? 1 : 0)) return 0;
    if (!bbuf_append_u8    (b, pdu->has_e2e ? 1 : 0))         return 0;
    if (pdu->has_e2e && pdu->e2e)
        if (!encode_e2e_protection(b, pdu->e2e))              return 0;
    if (!bbuf_append_u8    (b, pdu->has_secoc ? 1 : 0))       return 0;
    if (pdu->has_secoc && pdu->secoc)
        if (!encode_secoc_config(b, pdu->secoc))              return 0;
    if (!bbuf_append_u8    (b, pdu->has_multiplexing ? 1 : 0)) return 0;
    if (pdu->has_multiplexing && pdu->multiplex)
        if (!encode_multiplex_config(b, pdu->multiplex))      return 0;
    if (!bbuf_append_le32(b, (uint32_t)pdu->signal_count))    return 0;
    for (int32_t i = 0; i < pdu->signal_count; i++)
        if (!encode_signal_definition(b, &pdu->signals[i]))   return 0;
    if (!bbuf_append_le32(b, (uint32_t)pdu->contained_pdu_count)) return 0;
    for (int32_t i = 0; i < pdu->contained_pdu_count; i++)
        if (!encode_contained_pdu(b, &pdu->contained_pdus[i])) return 0;
    return 1;
}

/* §10.2 FrameDefinition */
static int encode_frame_definition(ByteBuf *b, const MeasFrameDefinition *fr,
                                    MeasBusType bus_type) {
    if (!bbuf_append_string(b, fr->name))                      return 0;
    if (!bbuf_append_le32  (b, fr->frame_id))                  return 0;
    if (!bbuf_append_le32  (b, (uint32_t)fr->payload_length))  return 0;
    if (!bbuf_append_u8    (b, fr->direction))                 return 0;
    uint8_t u16[2]; write_le16(u16, fr->flags);
    if (!bbuf_append(b, u16, 2))                               return 0;
    /* bus-specific fields (§10.2) */
    switch (bus_type) {
        case MEAS_BUS_CAN:
            if (!bbuf_append_u8(b, fr->bus.can.is_extended_id ? 1 : 0)) return 0;
            break;
        case MEAS_BUS_CAN_FD:
            if (!bbuf_append_u8(b, fr->bus.can_fd.is_extended_id       ? 1 : 0)) return 0;
            if (!bbuf_append_u8(b, fr->bus.can_fd.bit_rate_switch       ? 1 : 0)) return 0;
            if (!bbuf_append_u8(b, fr->bus.can_fd.error_state_indicator ? 1 : 0)) return 0;
            break;
        case MEAS_BUS_LIN:
            if (!bbuf_append_u8(b, fr->bus.lin.nad))           return 0;
            if (!bbuf_append_u8(b, fr->bus.lin.checksum_type)) return 0;
            break;
        case MEAS_BUS_FLEXRAY:
            if (!bbuf_append_u8(b, fr->bus.flexray.cycle_count)) return 0;
            if (!bbuf_append_u8(b, fr->bus.flexray.channel))     return 0;
            break;
        case MEAS_BUS_ETHERNET: {
            if (!bbuf_append(b, fr->bus.ethernet.mac_source, 6)) return 0;
            if (!bbuf_append(b, fr->bus.ethernet.mac_dest,   6)) return 0;
            write_le16(u16, fr->bus.ethernet.vlan_id);   if (!bbuf_append(b, u16, 2)) return 0;
            write_le16(u16, fr->bus.ethernet.ether_type); if (!bbuf_append(b, u16, 2)) return 0;
            break;
        }
        case MEAS_BUS_MOST:
            write_le16(u16, fr->bus.most.function_block); if (!bbuf_append(b, u16, 2)) return 0;
            if (!bbuf_append_u8(b, fr->bus.most.instance_id))    return 0;
            write_le16(u16, fr->bus.most.function_id);   if (!bbuf_append(b, u16, 2)) return 0;
            break;
        default: break;
    }
    if (!bbuf_append_le32(b, (uint32_t)fr->signal_count)) return 0;
    for (int32_t i = 0; i < fr->signal_count; i++)
        if (!encode_signal_definition(b, &fr->signals[i])) return 0;
    if (!bbuf_append_le32(b, (uint32_t)fr->pdu_count)) return 0;
    for (int32_t i = 0; i < fr->pdu_count; i++)
        if (!encode_pdu_definition(b, &fr->pdus[i])) return 0;
    return 1;
}

/* §10.10 ValueTable */
static int encode_value_table(ByteBuf *b, const MeasValueTable *vt) {
    if (!bbuf_append_string(b, vt->name))                 return 0;
    if (!bbuf_append_le32  (b, (uint32_t)vt->entry_count)) return 0;
    for (int32_t i = 0; i < vt->entry_count; i++) {
        if (!bbuf_append_le64  (b, (uint64_t)vt->entries[i].value)) return 0;
        if (!bbuf_append_string(b, vt->entries[i].description))      return 0;
    }
    return 1;
}

/* §10.1 BusConfig */
static int encode_bus_config(ByteBuf *b, const MeasBusConfig *cfg) {
    if (!bbuf_append_u8(b, (uint8_t)cfg->bus_type)) return 0;
    switch (cfg->bus_type) {
        case MEAS_BUS_CAN:
            if (!bbuf_append_u8  (b, cfg->u.can.is_extended_id ? 1 : 0)) return 0;
            if (!bbuf_append_le32(b, (uint32_t)cfg->u.can.baud_rate))    return 0;
            break;
        case MEAS_BUS_CAN_FD:
            if (!bbuf_append_u8  (b, cfg->u.can_fd.is_extended_id ? 1 : 0))  return 0;
            if (!bbuf_append_le32(b, (uint32_t)cfg->u.can_fd.arb_baud_rate))  return 0;
            if (!bbuf_append_le32(b, (uint32_t)cfg->u.can_fd.data_baud_rate)) return 0;
            break;
        case MEAS_BUS_LIN:
            if (!bbuf_append_le32(b, (uint32_t)cfg->u.lin.baud_rate)) return 0;
            if (!bbuf_append_u8  (b, cfg->u.lin.lin_version))         return 0;
            break;
        case MEAS_BUS_FLEXRAY:
            if (!bbuf_append_le32(b, (uint32_t)cfg->u.flexray.cycle_time_us))        return 0;
            if (!bbuf_append_le32(b, (uint32_t)cfg->u.flexray.macroticks_per_cycle)) return 0;
            break;
        default: break; /* NONE, ETHERNET, MOST have no extra fields */
    }
    return 1;
}

/* Public encode API */
int meas_bus_metadata_encode(const MeasBusMetadata *meta,
                              uint8_t **out_data, int32_t *out_len) {
    if (!meta || !out_data || !out_len) return -1;
    ByteBuf b; bbuf_init(&b);
    if (!bbuf_append_u8(&b, meta->format_version))                           goto fail;
    if (!encode_bus_config(&b, &meta->bus_config))                           goto fail;
    if (!bbuf_append_string(&b, meta->raw_frame_channel_name
                                ? meta->raw_frame_channel_name : ""))        goto fail;
    if (!bbuf_append_string(&b, meta->timestamp_channel_name
                                ? meta->timestamp_channel_name : ""))        goto fail;
    if (!bbuf_append_le32(&b, (uint32_t)meta->frame_count))                  goto fail;
    for (int32_t i = 0; i < meta->frame_count; i++)
        if (!encode_frame_definition(&b, &meta->frames[i],
                                     meta->bus_config.bus_type))              goto fail;
    if (!bbuf_append_le32(&b, (uint32_t)meta->value_table_count))            goto fail;
    for (int32_t i = 0; i < meta->value_table_count; i++)
        if (!encode_value_table(&b, &meta->value_tables[i]))                 goto fail;
    *out_data = b.data;
    *out_len  = (int32_t)b.size;
    return 0;
fail:
    bbuf_free(&b); return -1;
}

/* ── §10 decode helpers ──────────────────────────────────────────────────── */

static int decode_multiplex_condition(const uint8_t *buf, size_t bufsz, size_t *off,
                                       MeasMultiplexCondition **out) {
    MeasMultiplexCondition *mc = (MeasMultiplexCondition *)calloc(1, sizeof(*mc));
    if (!mc) return 0;
    *out = mc;
    mc->multiplexer_signal_name = decode_string(buf, bufsz, off);
    if (!mc->multiplexer_signal_name) return 0;
    if (*off + 17 > bufsz) return 0;
    mc->low_value  = (int64_t)read_le64(buf + *off); *off += 8;
    mc->high_value = (int64_t)read_le64(buf + *off); *off += 8;
    mc->has_parent = buf[(*off)++];
    if (mc->has_parent)
        if (!decode_multiplex_condition(buf, bufsz, off, &mc->parent)) return 0;
    return 1;
}

static int decode_signal_definition(const uint8_t *buf, size_t bufsz, size_t *off,
                                     MeasSignalDefinition *sig) {
    sig->name = decode_string(buf, bufsz, off);
    if (!sig->name) return 0;
    if (*off + 10 > bufsz) return 0;
    sig->start_bit   = (int32_t)read_le32(buf + *off); *off += 4;
    sig->bit_length  = (int32_t)read_le32(buf + *off); *off += 4;
    sig->byte_order  = buf[(*off)++];
    sig->signal_type = buf[(*off)++];
    if (*off + 17 > bufsz) return 0;
    sig->factor = le_bytes_to_f64(buf + *off); *off += 8;
    sig->offset = le_bytes_to_f64(buf + *off); *off += 8;
    sig->min_max_flags = buf[(*off)++];
    if (sig->min_max_flags & 0x01) {
        if (*off + 8 > bufsz) return 0;
        sig->min_value = le_bytes_to_f64(buf + *off); *off += 8;
    }
    if (sig->min_max_flags & 0x02) {
        if (*off + 8 > bufsz) return 0;
        sig->max_value = le_bytes_to_f64(buf + *off); *off += 8;
    }
    if (*off >= bufsz) return 0;
    sig->has_unit = buf[(*off)++];
    if (sig->has_unit) {
        sig->unit = decode_string(buf, bufsz, off);
        if (!sig->unit) return 0;
    }
    if (*off + 2 > bufsz) return 0;
    sig->is_multiplexer          = buf[(*off)++];
    sig->has_multiplex_condition = buf[(*off)++];
    if (sig->has_multiplex_condition)
        if (!decode_multiplex_condition(buf, bufsz, off,
                                        &sig->multiplex_condition)) return 0;
    if (*off + 4 > bufsz) return 0;
    sig->value_desc_count = (int32_t)read_le32(buf + *off); *off += 4;
    if (sig->value_desc_count < 0 || sig->value_desc_count > 100000) return 0;
    if (sig->value_desc_count > 0) {
        sig->value_descs = (MeasValueDescription *)calloc(
            (size_t)sig->value_desc_count, sizeof(MeasValueDescription));
        if (!sig->value_descs) return 0;
        for (int32_t i = 0; i < sig->value_desc_count; i++) {
            if (*off + 8 > bufsz) return 0;
            sig->value_descs[i].value = (int64_t)read_le64(buf + *off); *off += 8;
            sig->value_descs[i].description = decode_string(buf, bufsz, off);
            if (!sig->value_descs[i].description) return 0;
        }
    }
    return 1;
}

static int decode_e2e_protection(const uint8_t *buf, size_t bufsz, size_t *off,
                                  MeasE2EProtection **out) {
    if (*off + 25 > bufsz) return 0;
    MeasE2EProtection *e = (MeasE2EProtection *)calloc(1, sizeof(*e));
    if (!e) return 0;
    *out = e;
    e->profile               =        buf[(*off)++];
    e->crc_start_bit         = (int32_t)read_le32(buf + *off); *off += 4;
    e->crc_bit_length        = (int32_t)read_le32(buf + *off); *off += 4;
    e->counter_start_bit     = (int32_t)read_le32(buf + *off); *off += 4;
    e->counter_bit_length    = (int32_t)read_le32(buf + *off); *off += 4;
    e->data_id               = read_le32(buf + *off); *off += 4;
    e->crc_polynomial        = read_le32(buf + *off); *off += 4;
    return 1;
}

static int decode_secoc_config(const uint8_t *buf, size_t bufsz, size_t *off,
                                MeasSecOcConfig **out) {
    if (*off + 44 > bufsz) return 0;
    MeasSecOcConfig *s = (MeasSecOcConfig *)calloc(1, sizeof(*s));
    if (!s) return 0;
    *out = s;
    s->algorithm                    =        buf[(*off)++];
    s->freshness_start_bit          = (int32_t)read_le32(buf + *off); *off += 4;
    s->freshness_truncated_length   = (int32_t)read_le32(buf + *off); *off += 4;
    s->freshness_full_length        = (int32_t)read_le32(buf + *off); *off += 4;
    s->freshness_type               =        buf[(*off)++];
    s->mac_start_bit                = (int32_t)read_le32(buf + *off); *off += 4;
    s->mac_truncated_length         = (int32_t)read_le32(buf + *off); *off += 4;
    s->mac_full_length              = (int32_t)read_le32(buf + *off); *off += 4;
    s->authen_payload_length        = (int32_t)read_le32(buf + *off); *off += 4;
    s->data_id                      = read_le32(buf + *off); *off += 4;
    s->auth_build_attempts          = (int32_t)read_le32(buf + *off); *off += 4;
    s->use_freshness_value_manager  =        buf[(*off)++];
    s->key_id                       = read_le32(buf + *off); *off += 4;
    return 1;
}

static int decode_multiplex_config(const uint8_t *buf, size_t bufsz, size_t *off,
                                    MeasMultiplexConfig **out) {
    MeasMultiplexConfig *mx = (MeasMultiplexConfig *)calloc(1, sizeof(*mx));
    if (!mx) return 0;
    *out = mx;
    mx->multiplexer_signal_name = decode_string(buf, bufsz, off);
    if (!mx->multiplexer_signal_name) return 0;
    if (*off + 4 > bufsz) return 0;
    mx->group_count = (int32_t)read_le32(buf + *off); *off += 4;
    if (mx->group_count < 0 || mx->group_count > 100000) return 0;
    if (mx->group_count > 0) {
        mx->groups = (MeasMuxGroup *)calloc((size_t)mx->group_count, sizeof(MeasMuxGroup));
        if (!mx->groups) return 0;
        for (int32_t i = 0; i < mx->group_count; i++) {
            MeasMuxGroup *grp = &mx->groups[i];
            if (*off + 12 > bufsz) return 0;
            grp->mux_value = (int64_t)read_le64(buf + *off); *off += 8;
            grp->signal_name_count = (int32_t)read_le32(buf + *off); *off += 4;
            if (grp->signal_name_count < 0 || grp->signal_name_count > 100000) return 0;
            if (grp->signal_name_count > 0) {
                grp->signal_names = (char **)calloc((size_t)grp->signal_name_count, sizeof(char *));
                if (!grp->signal_names) return 0;
                for (int32_t j = 0; j < grp->signal_name_count; j++) {
                    grp->signal_names[j] = decode_string(buf, bufsz, off);
                    if (!grp->signal_names[j]) return 0;
                }
            }
        }
    }
    return 1;
}

/* Forward declaration */
static int decode_pdu_definition(const uint8_t *buf, size_t bufsz, size_t *off,
                                  MeasPduDefinition *pdu);

static int decode_contained_pdu(const uint8_t *buf, size_t bufsz, size_t *off,
                                 MeasContainedPdu *cpdu) {
    cpdu->name = decode_string(buf, bufsz, off);
    if (!cpdu->name) return 0;
    if (*off + 12 > bufsz) return 0;
    cpdu->header_id    = read_le32(buf + *off); *off += 4;
    cpdu->length       = (int32_t)read_le32(buf + *off); *off += 4;
    cpdu->signal_count = (int32_t)read_le32(buf + *off); *off += 4;
    if (cpdu->signal_count < 0 || cpdu->signal_count > 100000) return 0;
    if (cpdu->signal_count > 0) {
        cpdu->signals = (MeasSignalDefinition *)calloc(
            (size_t)cpdu->signal_count, sizeof(MeasSignalDefinition));
        if (!cpdu->signals) return 0;
        for (int32_t i = 0; i < cpdu->signal_count; i++)
            if (!decode_signal_definition(buf, bufsz, off, &cpdu->signals[i])) return 0;
    }
    return 1;
}

static int decode_pdu_definition(const uint8_t *buf, size_t bufsz, size_t *off,
                                  MeasPduDefinition *pdu) {
    pdu->name = decode_string(buf, bufsz, off);
    if (!pdu->name) return 0;
    if (*off + 13 > bufsz) return 0;
    pdu->pdu_id          = read_le32(buf + *off); *off += 4;
    pdu->byte_offset     = (int32_t)read_le32(buf + *off); *off += 4;
    pdu->length          = (int32_t)read_le32(buf + *off); *off += 4;
    pdu->is_container_pdu = buf[(*off)++];
    pdu->has_e2e          = buf[(*off)++];
    if (pdu->has_e2e)
        if (!decode_e2e_protection(buf, bufsz, off, &pdu->e2e)) return 0;
    if (*off >= bufsz) return 0;
    pdu->has_secoc = buf[(*off)++];
    if (pdu->has_secoc)
        if (!decode_secoc_config(buf, bufsz, off, &pdu->secoc)) return 0;
    if (*off >= bufsz) return 0;
    pdu->has_multiplexing = buf[(*off)++];
    if (pdu->has_multiplexing)
        if (!decode_multiplex_config(buf, bufsz, off, &pdu->multiplex)) return 0;
    if (*off + 4 > bufsz) return 0;
    pdu->signal_count = (int32_t)read_le32(buf + *off); *off += 4;
    if (pdu->signal_count < 0 || pdu->signal_count > 100000) return 0;
    if (pdu->signal_count > 0) {
        pdu->signals = (MeasSignalDefinition *)calloc(
            (size_t)pdu->signal_count, sizeof(MeasSignalDefinition));
        if (!pdu->signals) return 0;
        for (int32_t i = 0; i < pdu->signal_count; i++)
            if (!decode_signal_definition(buf, bufsz, off, &pdu->signals[i])) return 0;
    }
    if (*off + 4 > bufsz) return 0;
    pdu->contained_pdu_count = (int32_t)read_le32(buf + *off); *off += 4;
    if (pdu->contained_pdu_count < 0 || pdu->contained_pdu_count > 100000) return 0;
    if (pdu->contained_pdu_count > 0) {
        pdu->contained_pdus = (MeasContainedPdu *)calloc(
            (size_t)pdu->contained_pdu_count, sizeof(MeasContainedPdu));
        if (!pdu->contained_pdus) return 0;
        for (int32_t i = 0; i < pdu->contained_pdu_count; i++)
            if (!decode_contained_pdu(buf, bufsz, off, &pdu->contained_pdus[i])) return 0;
    }
    return 1;
}

static int decode_frame_definition(const uint8_t *buf, size_t bufsz, size_t *off,
                                    MeasFrameDefinition *fr, MeasBusType bus_type) {
    fr->name = decode_string(buf, bufsz, off);
    if (!fr->name) return 0;
    if (*off + 11 > bufsz) return 0;
    fr->frame_id       = read_le32(buf + *off); *off += 4;
    fr->payload_length = (int32_t)read_le32(buf + *off); *off += 4;
    fr->direction      = buf[(*off)++];
    fr->flags          = read_le16(buf + *off); *off += 2;
    /* bus-specific fields */
    switch (bus_type) {
        case MEAS_BUS_CAN:
            if (*off >= bufsz) return 0;
            fr->bus.can.is_extended_id = buf[(*off)++];
            break;
        case MEAS_BUS_CAN_FD:
            if (*off + 3 > bufsz) return 0;
            fr->bus.can_fd.is_extended_id       = buf[(*off)++];
            fr->bus.can_fd.bit_rate_switch       = buf[(*off)++];
            fr->bus.can_fd.error_state_indicator = buf[(*off)++];
            break;
        case MEAS_BUS_LIN:
            if (*off + 2 > bufsz) return 0;
            fr->bus.lin.nad           = buf[(*off)++];
            fr->bus.lin.checksum_type = buf[(*off)++];
            break;
        case MEAS_BUS_FLEXRAY:
            if (*off + 2 > bufsz) return 0;
            fr->bus.flexray.cycle_count = buf[(*off)++];
            fr->bus.flexray.channel     = buf[(*off)++];
            break;
        case MEAS_BUS_ETHERNET:
            if (*off + 16 > bufsz) return 0;
            memcpy(fr->bus.ethernet.mac_source, buf + *off, 6); *off += 6;
            memcpy(fr->bus.ethernet.mac_dest,   buf + *off, 6); *off += 6;
            fr->bus.ethernet.vlan_id    = read_le16(buf + *off); *off += 2;
            fr->bus.ethernet.ether_type = read_le16(buf + *off); *off += 2;
            break;
        case MEAS_BUS_MOST:
            if (*off + 5 > bufsz) return 0;
            fr->bus.most.function_block = read_le16(buf + *off); *off += 2;
            fr->bus.most.instance_id    = buf[(*off)++];
            fr->bus.most.function_id    = read_le16(buf + *off); *off += 2;
            break;
        default: break;
    }
    if (*off + 4 > bufsz) return 0;
    fr->signal_count = (int32_t)read_le32(buf + *off); *off += 4;
    if (fr->signal_count < 0 || fr->signal_count > 100000) return 0;
    if (fr->signal_count > 0) {
        fr->signals = (MeasSignalDefinition *)calloc(
            (size_t)fr->signal_count, sizeof(MeasSignalDefinition));
        if (!fr->signals) return 0;
        for (int32_t i = 0; i < fr->signal_count; i++)
            if (!decode_signal_definition(buf, bufsz, off, &fr->signals[i])) return 0;
    }
    if (*off + 4 > bufsz) return 0;
    fr->pdu_count = (int32_t)read_le32(buf + *off); *off += 4;
    if (fr->pdu_count < 0 || fr->pdu_count > 100000) return 0;
    if (fr->pdu_count > 0) {
        fr->pdus = (MeasPduDefinition *)calloc(
            (size_t)fr->pdu_count, sizeof(MeasPduDefinition));
        if (!fr->pdus) return 0;
        for (int32_t i = 0; i < fr->pdu_count; i++)
            if (!decode_pdu_definition(buf, bufsz, off, &fr->pdus[i])) return 0;
    }
    return 1;
}

static int decode_value_table(const uint8_t *buf, size_t bufsz, size_t *off,
                               MeasValueTable *vt) {
    vt->name = decode_string(buf, bufsz, off);
    if (!vt->name) return 0;
    if (*off + 4 > bufsz) return 0;
    vt->entry_count = (int32_t)read_le32(buf + *off); *off += 4;
    if (vt->entry_count < 0 || vt->entry_count > 1000000) return 0;
    if (vt->entry_count > 0) {
        vt->entries = (MeasValueTableEntry *)calloc(
            (size_t)vt->entry_count, sizeof(MeasValueTableEntry));
        if (!vt->entries) return 0;
        for (int32_t i = 0; i < vt->entry_count; i++) {
            if (*off + 8 > bufsz) return 0;
            vt->entries[i].value = (int64_t)read_le64(buf + *off); *off += 8;
            vt->entries[i].description = decode_string(buf, bufsz, off);
            if (!vt->entries[i].description) return 0;
        }
    }
    return 1;
}

/* ── §10 free helpers ────────────────────────────────────────────────────── */

static void free_multiplex_condition(MeasMultiplexCondition *mc) {
    if (!mc) return;
    free(mc->multiplexer_signal_name);
    free_multiplex_condition(mc->parent);
    free(mc->parent);
    /* mc itself is freed by the caller */
}

static void free_signal_definition(MeasSignalDefinition *sig) {
    free(sig->name);
    free(sig->unit);
    if (sig->multiplex_condition) {
        free_multiplex_condition(sig->multiplex_condition);
        free(sig->multiplex_condition);
    }
    for (int32_t i = 0; i < sig->value_desc_count; i++)
        free(sig->value_descs[i].description);
    free(sig->value_descs);
}

static void free_multiplex_config(MeasMultiplexConfig *mx) {
    if (!mx) return;
    free(mx->multiplexer_signal_name);
    for (int32_t i = 0; i < mx->group_count; i++) {
        for (int32_t j = 0; j < mx->groups[i].signal_name_count; j++)
            free(mx->groups[i].signal_names[j]);
        free(mx->groups[i].signal_names);
    }
    free(mx->groups);
}

static void free_contained_pdu(MeasContainedPdu *cpdu) {
    free(cpdu->name);
    for (int32_t i = 0; i < cpdu->signal_count; i++)
        free_signal_definition(&cpdu->signals[i]);
    free(cpdu->signals);
}

static void free_pdu_definition(MeasPduDefinition *pdu) {
    free(pdu->name);
    if (pdu->e2e)     { free(pdu->e2e); }
    if (pdu->secoc)   { free(pdu->secoc); }
    if (pdu->multiplex) {
        free_multiplex_config(pdu->multiplex);
        free(pdu->multiplex);
    }
    for (int32_t i = 0; i < pdu->signal_count; i++)
        free_signal_definition(&pdu->signals[i]);
    free(pdu->signals);
    for (int32_t i = 0; i < pdu->contained_pdu_count; i++)
        free_contained_pdu(&pdu->contained_pdus[i]);
    free(pdu->contained_pdus);
}

static void free_frame_definition(MeasFrameDefinition *fr) {
    free(fr->name);
    for (int32_t i = 0; i < fr->signal_count; i++)
        free_signal_definition(&fr->signals[i]);
    free(fr->signals);
    for (int32_t i = 0; i < fr->pdu_count; i++)
        free_pdu_definition(&fr->pdus[i]);
    free(fr->pdus);
}

static void free_value_table(MeasValueTable *vt) {
    free(vt->name);
    for (int32_t i = 0; i < vt->entry_count; i++)
        free(vt->entries[i].description);
    free(vt->entries);
}

/* Public decode / free API */
int meas_bus_metadata_decode(const uint8_t *data, int32_t len,
                              MeasBusMetadata **out_meta) {
    if (!data || len < 1 || !out_meta) return -1;
    MeasBusMetadata *m = (MeasBusMetadata *)calloc(1, sizeof(*m));
    if (!m) return -1;
    *out_meta = m;
    size_t bufsz = (size_t)len;
    size_t off   = 0;
    m->format_version = data[off++];
    /* BusConfig */
    if (off >= bufsz) return -1;
    m->bus_config.bus_type = (MeasBusType)data[off++];
    switch (m->bus_config.bus_type) {
        case MEAS_BUS_CAN:
            if (off + 5 > bufsz) return -1;
            m->bus_config.u.can.is_extended_id = data[off++];
            m->bus_config.u.can.baud_rate      = (int32_t)read_le32(data + off); off += 4;
            break;
        case MEAS_BUS_CAN_FD:
            if (off + 9 > bufsz) return -1;
            m->bus_config.u.can_fd.is_extended_id  = data[off++];
            m->bus_config.u.can_fd.arb_baud_rate   = (int32_t)read_le32(data + off); off += 4;
            m->bus_config.u.can_fd.data_baud_rate  = (int32_t)read_le32(data + off); off += 4;
            break;
        case MEAS_BUS_LIN:
            if (off + 5 > bufsz) return -1;
            m->bus_config.u.lin.baud_rate    = (int32_t)read_le32(data + off); off += 4;
            m->bus_config.u.lin.lin_version  = data[off++];
            break;
        case MEAS_BUS_FLEXRAY:
            if (off + 8 > bufsz) return -1;
            m->bus_config.u.flexray.cycle_time_us        = (int32_t)read_le32(data + off); off += 4;
            m->bus_config.u.flexray.macroticks_per_cycle = (int32_t)read_le32(data + off); off += 4;
            break;
        default: break;
    }
    m->raw_frame_channel_name = decode_string(data, bufsz, &off);
    if (!m->raw_frame_channel_name) return -1;
    m->timestamp_channel_name = decode_string(data, bufsz, &off);
    if (!m->timestamp_channel_name) return -1;
    if (off + 4 > bufsz) return -1;
    m->frame_count = (int32_t)read_le32(data + off); off += 4;
    if (m->frame_count < 0 || m->frame_count > 100000) return -1;
    if (m->frame_count > 0) {
        m->frames = (MeasFrameDefinition *)calloc((size_t)m->frame_count, sizeof(*m->frames));
        if (!m->frames) return -1;
        for (int32_t i = 0; i < m->frame_count; i++)
            if (!decode_frame_definition(data, bufsz, &off, &m->frames[i],
                                          m->bus_config.bus_type)) return -1;
    }
    if (off + 4 > bufsz) return -1;
    m->value_table_count = (int32_t)read_le32(data + off); off += 4;
    if (m->value_table_count < 0 || m->value_table_count > 100000) return -1;
    if (m->value_table_count > 0) {
        m->value_tables = (MeasValueTable *)calloc(
            (size_t)m->value_table_count, sizeof(*m->value_tables));
        if (!m->value_tables) return -1;
        for (int32_t i = 0; i < m->value_table_count; i++)
            if (!decode_value_table(data, bufsz, &off, &m->value_tables[i])) return -1;
    }
    return 0;
}

void meas_bus_metadata_free(MeasBusMetadata *meta) {
    if (!meta) return;
    free(meta->raw_frame_channel_name);
    free(meta->timestamp_channel_name);
    for (int32_t i = 0; i < meta->frame_count; i++)
        free_frame_definition(&meta->frames[i]);
    free(meta->frames);
    for (int32_t i = 0; i < meta->value_table_count; i++)
        free_value_table(&meta->value_tables[i]);
    free(meta->value_tables);
    free(meta);
}

/* ── §10 Group property writer helpers ──────────────────────────────────── */

int meas_group_set_property_bin(MeasGroupWriter *g, const char *key,
                                 const uint8_t *data, int32_t len) {
    if (!g || !key || (!data && len > 0)) return -1;
    if (g->sealed) return -1;
    if (!bbuf_append_string(&g->props_blob, key))                  return -1;
    if (!bbuf_append_u8    (&g->props_blob, (uint8_t)MEAS_BINARY)) return -1;
    if (!bbuf_append_le32  (&g->props_blob, (uint32_t)len))        return -1;
    if (len > 0 && !bbuf_append(&g->props_blob, data, (size_t)len)) return -1;
    g->property_count++;
    return 0;
}

int meas_group_set_property_str(MeasGroupWriter *g, const char *key, const char *value) {
    if (!g || !key || !value) return -1;
    if (g->sealed) return -1;
    if (!bbuf_append_string(&g->props_blob, key))                   return -1;
    if (!bbuf_append_u8    (&g->props_blob, (uint8_t)MEAS_STRING))  return -1;
    if (!bbuf_append_string(&g->props_blob, value))                 return -1;
    g->property_count++;
    return 0;
}

int meas_group_set_property_i32(MeasGroupWriter *g, const char *key, int32_t value) {
    if (!g || !key) return -1;
    if (g->sealed) return -1;
    if (!bbuf_append_string(&g->props_blob, key))                  return -1;
    if (!bbuf_append_u8    (&g->props_blob, (uint8_t)MEAS_INT32))  return -1;
    if (!bbuf_append_le32  (&g->props_blob, (uint32_t)value))      return -1;
    g->property_count++;
    return 0;
}

int meas_group_set_property_f64(MeasGroupWriter *g, const char *key, double value) {
    if (!g || !key) return -1;
    if (g->sealed) return -1;
    if (!bbuf_append_string(&g->props_blob, key))                   return -1;
    if (!bbuf_append_u8    (&g->props_blob, (uint8_t)MEAS_FLOAT64)) return -1;
    uint8_t vb[8]; f64_to_le_bytes(vb, value);
    if (!bbuf_append(&g->props_blob, vb, 8))                        return -1;
    g->property_count++;
    return 0;
}

int meas_channel_set_property_str(MeasChannelWriter *ch, const char *key, const char *value) {
    if (!ch || !key || !value) return -1;
    if (ch->sealed) return -1;
    if (!bbuf_append_string(&ch->props_blob, key))                   return -1;
    if (!bbuf_append_u8    (&ch->props_blob, (uint8_t)MEAS_STRING))  return -1;
    if (!bbuf_append_string(&ch->props_blob, value))                 return -1;
    ch->property_count++;
    return 0;
}

int meas_channel_set_property_i32(MeasChannelWriter *ch, const char *key, int32_t value) {
    if (!ch || !key) return -1;
    if (ch->sealed) return -1;
    if (!bbuf_append_string(&ch->props_blob, key))                  return -1;
    if (!bbuf_append_u8    (&ch->props_blob, (uint8_t)MEAS_INT32))  return -1;
    if (!bbuf_append_le32  (&ch->props_blob, (uint32_t)value))      return -1;
    ch->property_count++;
    return 0;
}

int meas_channel_set_property_f64(MeasChannelWriter *ch, const char *key, double value) {
    if (!ch || !key) return -1;
    if (ch->sealed) return -1;
    if (!bbuf_append_string(&ch->props_blob, key))                   return -1;
    if (!bbuf_append_u8    (&ch->props_blob, (uint8_t)MEAS_FLOAT64)) return -1;
    uint8_t vb[8]; f64_to_le_bytes(vb, value);
    if (!bbuf_append(&ch->props_blob, vb, 8))                        return -1;
    ch->property_count++;
    return 0;
}

int meas_group_set_bus_def(MeasGroupWriter *group, const MeasBusMetadata *meta) {
    if (!group || !meta) return -1;
    uint8_t *blob = NULL; int32_t blen = 0;
    if (meas_bus_metadata_encode(meta, &blob, &blen) != 0) return -1;
    int rc = meas_group_set_property_bin(group, "MEAS.bus_def", blob, blen);
    free(blob);
    return rc;
}

/* ── §10 Group property reader helper ───────────────────────────────────── */

int meas_group_read_bus_def(const MeasGroupData *group, MeasBusMetadata **out_meta) {
    if (!group || !out_meta) return -1;
    for (int i = 0; i < group->property_count; i++) {
        const MeasProperty *p = &group->properties[i];
        if (strcmp(p->key, "MEAS.bus_def") == 0 && p->type == MEAS_BINARY) {
            return meas_bus_metadata_decode(
                (const uint8_t *)p->value.bin.data, p->value.bin.length, out_meta);
        }
    }
    return -1; /* property not found */
}

/* ── §11 Typed frame write helpers ──────────────────────────────────────── */

int meas_channel_write_can_frame(MeasChannelWriter *ch, const MeasCanFrame *f) {
    if (!ch || ch->dtype != MEAS_BINARY || !f) return -1;
    /* wire: [uint32: arb_id][byte: dlc][byte: flags][payload: dlc bytes] */
    uint8_t hdr[6];
    write_le32(hdr, f->arb_id);
    hdr[4] = f->dlc;
    hdr[5] = f->flags;
    int32_t wire_len = 6 + f->dlc;
    uint8_t lbuf[4]; write_le32(lbuf, (uint32_t)wire_len);
    if (!bbuf_append(&ch->buf, lbuf, 4)) return -1;
    if (!bbuf_append(&ch->buf, hdr,  6)) return -1;
    if (f->dlc > 0 && !bbuf_append(&ch->buf, f->payload, f->dlc)) return -1;
    ch->sample_count_pending++; return 0;
}

int meas_channel_write_lin_frame(MeasChannelWriter *ch, const MeasLinFrame *f) {
    if (!ch || ch->dtype != MEAS_BINARY || !f) return -1;
    /* wire: [byte: frame_id][byte: dlc][byte: nad][byte: checksum_type][payload: dlc bytes] */
    uint8_t hdr[4] = { f->frame_id, f->dlc, f->nad, f->checksum_type };
    int32_t wire_len = 4 + f->dlc;
    uint8_t lbuf[4]; write_le32(lbuf, (uint32_t)wire_len);
    if (!bbuf_append(&ch->buf, lbuf, 4)) return -1;
    if (!bbuf_append(&ch->buf, hdr,  4)) return -1;
    if (f->dlc > 0 && !bbuf_append(&ch->buf, f->payload, f->dlc)) return -1;
    ch->sample_count_pending++; return 0;
}

int meas_channel_write_flexray_frame(MeasChannelWriter *ch, const MeasFlexRayFrame *f) {
    if (!ch || ch->dtype != MEAS_BINARY || !f) return -1;
    /* wire: [uint16: slot_id][byte: cycle_count][byte: channel_flags]
             [uint16: payload_length][payload: payload_length bytes] */
    uint8_t hdr[6];
    write_le16(hdr,     f->slot_id);
    hdr[2] = f->cycle_count;
    hdr[3] = f->channel_flags;
    write_le16(hdr + 4, f->payload_length);
    int32_t wire_len = 6 + f->payload_length;
    uint8_t lbuf[4]; write_le32(lbuf, (uint32_t)wire_len);
    if (!bbuf_append(&ch->buf, lbuf, 4)) return -1;
    if (!bbuf_append(&ch->buf, hdr,  6)) return -1;
    if (f->payload_length > 0 && !bbuf_append(&ch->buf, f->payload, f->payload_length)) return -1;
    ch->sample_count_pending++; return 0;
}

int meas_channel_write_ethernet_frame(MeasChannelWriter *ch, const MeasEthernetFrame *f) {
    if (!ch || ch->dtype != MEAS_BINARY || !f) return -1;
    /* wire: [6B: mac_dest][6B: mac_src][uint16: ether_type][uint16: vlan_id]
             [uint16: payload_length][payload: payload_length bytes] */
    uint8_t hdr[18];
    memcpy(hdr,      f->mac_dest, 6);
    memcpy(hdr + 6,  f->mac_src,  6);
    write_le16(hdr + 12, f->ether_type);
    write_le16(hdr + 14, f->vlan_id);
    write_le16(hdr + 16, f->payload_length);
    int32_t wire_len = 18 + f->payload_length;
    uint8_t lbuf[4]; write_le32(lbuf, (uint32_t)wire_len);
    if (!bbuf_append(&ch->buf, lbuf, 4)) return -1;
    if (!bbuf_append(&ch->buf, hdr, 18)) return -1;
    if (f->payload_length > 0 && !bbuf_append(&ch->buf, f->payload, f->payload_length)) return -1;
    ch->sample_count_pending++; return 0;
}

/* ── §11 Typed frame read helpers ────────────────────────────────────────── */

/* Helper: advance *state past the frame-length prefix and return a pointer to
   the frame data.  Returns NULL when exhausted or on error, setting *ok = 0.
   Sets *frame_len to the wire frame byte count (excl. the 4-byte prefix). */
static const uint8_t *next_raw_frame(const MeasChannelData *ch, int64_t *state,
                                      int32_t *frame_len, int *ok) {
    *ok = 0;
    if (!ch || ch->data_type != MEAS_BINARY || !state || !frame_len) return NULL;
    if (*state >= ch->data_size) { *ok = 2; return NULL; } /* exhausted */
    if (*state + 4 > ch->data_size) return NULL;
    int32_t len = (int32_t)read_le32(ch->data + (size_t)*state);
    *state += 4;
    if (len < 0 || *state + len > ch->data_size) return NULL;
    const uint8_t *p = ch->data + (size_t)*state;
    *state += len;
    *frame_len = len;
    *ok = 1;
    return p;
}

int meas_channel_next_can_frame(const MeasChannelData *ch, int64_t *state,
                                 MeasCanFrame *out) {
    if (!out) return -1;
    int ok; int32_t flen;
    const uint8_t *p = next_raw_frame(ch, state, &flen, &ok);
    if (ok == 2) return 0;  /* exhausted */
    if (!p || flen < 6)     return -1;
    out->arb_id = read_le32(p);
    out->dlc    = p[4];
    out->flags  = p[5];
    if (out->dlc > MEAS_CAN_PAYLOAD_MAX) out->dlc = MEAS_CAN_PAYLOAD_MAX;
    if ((int32_t)(6 + out->dlc) > flen) return -1;
    memcpy(out->payload, p + 6, out->dlc);
    return 1;
}

int meas_channel_next_lin_frame(const MeasChannelData *ch, int64_t *state,
                                 MeasLinFrame *out) {
    if (!out) return -1;
    int ok; int32_t flen;
    const uint8_t *p = next_raw_frame(ch, state, &flen, &ok);
    if (ok == 2) return 0;
    if (!p || flen < 4)  return -1;
    out->frame_id      = p[0];
    out->dlc           = p[1];
    out->nad           = p[2];
    out->checksum_type = p[3];
    if (out->dlc > MEAS_LIN_PAYLOAD_MAX) out->dlc = MEAS_LIN_PAYLOAD_MAX;
    if ((int32_t)(4 + out->dlc) > flen) return -1;
    memcpy(out->payload, p + 4, out->dlc);
    return 1;
}

int meas_channel_next_flexray_frame(const MeasChannelData *ch, int64_t *state,
                                     MeasFlexRayFrame *out) {
    if (!out) return -1;
    int ok; int32_t flen;
    const uint8_t *p = next_raw_frame(ch, state, &flen, &ok);
    if (ok == 2) return 0;
    if (!p || flen < 6)  return -1;
    out->slot_id        = read_le16(p);
    out->cycle_count    = p[2];
    out->channel_flags  = p[3];
    out->payload_length = read_le16(p + 4);
    if ((int32_t)(6 + out->payload_length) > flen) return -1;
    out->payload = (out->payload_length > 0) ? p + 6 : NULL; /* zero-copy */
    return 1;
}

int meas_channel_next_ethernet_frame(const MeasChannelData *ch, int64_t *state,
                                      MeasEthernetFrame *out) {
    if (!out) return -1;
    int ok; int32_t flen;
    const uint8_t *p = next_raw_frame(ch, state, &flen, &ok);
    if (ok == 2) return 0;
    if (!p || flen < 18)  return -1;
    memcpy(out->mac_dest, p,     6);
    memcpy(out->mac_src,  p + 6, 6);
    out->ether_type     = read_le16(p + 12);
    out->vlan_id        = read_le16(p + 14);
    out->payload_length = read_le16(p + 16);
    if ((int32_t)(18 + out->payload_length) > flen) return -1;
    out->payload = (out->payload_length > 0) ? p + 18 : NULL; /* zero-copy */
    return 1;
}

/* ── Reader internals ────────────────────────────────────────────────────── */

struct MeasReader {
    MeasGroupData *groups;
    int            group_count;
    int            file_prop_count;
    MeasProperty  *file_props;
    /* Memory-mapped file handles */
#ifdef _WIN32
    HANDLE  mmap_file;
    HANDLE  mmap_mapping;
    void   *mmap_base;
#else
    void   *mmap_base;
    size_t  mmap_size;
#endif
};

/* Append raw bytes to a channel's data array */
/* Append data to channel. For the first append of uncompressed mmap data,
   store a borrowed pointer (zero-copy). On subsequent appends, transition
   to an owned buffer. The 'borrowed' flag indicates src points into stable
   memory (mmap) that will outlive the reader. */
static int channel_append_data(MeasChannelData *ch, const uint8_t *src, size_t len, int borrowed) {
    if (ch->data == NULL && borrowed) {
        /* Zero-copy: point directly into mmap buffer */
        ch->data = (uint8_t *)src; /* const-cast safe: read-only via API */
        ch->data_size = (int64_t)len;
        ch->data_owned = 0;
        return 1;
    }
    /* Must copy: either second append or non-borrowed (decompressed) data */
    if (!ch->data_owned && ch->data != NULL) {
        /* Transition from borrowed to owned: copy existing data first */
        uint8_t *p = (uint8_t *)malloc((size_t)ch->data_size + len);
        if (!p) return 0;
        memcpy(p, ch->data, (size_t)ch->data_size);
        memcpy(p + ch->data_size, src, len);
        ch->data = p;
        ch->data_owned = 1;
    } else {
        uint8_t *p = (uint8_t *)realloc(ch->data, (size_t)ch->data_size + len);
        if (!p) return 0;
        memcpy(p + ch->data_size, src, len);
        ch->data = p;
        ch->data_owned = 1;
    }
    ch->data_size += (int64_t)len;
    return 1;
}

/* Decode metadata segment content (§6) and populate reader groups.
   When has_file_properties is set, the content starts with
   [uint8 metaMajor][uint8 metaMinor][file properties] before groups. */
static int decode_metadata_segment(MeasReader *r, const uint8_t *buf, size_t bufsz,
                                    int has_file_properties) {
    size_t off = 0;

    if (has_file_properties) {
        /* Read extended metadata version */
        if (off + 2 > bufsz) return 0;
        uint8_t metaMajor = buf[off++];
        uint8_t metaMinor = buf[off++];
        if (metaMajor != 0 || metaMinor > 1) return 0;
        /* Decode file-level properties */
        if (!decode_properties(buf, bufsz, &off, &r->file_props, &r->file_prop_count))
            return 0;
    }

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
   all_channels is a flat array indexed by global channel index.
   'borrowed' indicates buf points into stable mmap memory (zero-copy eligible). */
static int decode_data_segment(const uint8_t *buf, size_t bufsz,
                                MeasChannelData **all_channels, int total_channels,
                                int borrowed) {
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
            if (!channel_append_data(ch, buf + off, (size_t)data_len, borrowed)) return 0;
            ch->sample_count += samples;
        }
        off += (size_t)data_len;
    }
    return 1;
}

/* ── Reader public API ───────────────────────────────────────────────────── */

MeasReader *meas_reader_open(const char *path) {
    if (!path) return NULL;

    /* Memory-map the file for efficient read access */
    uint8_t *filebuf = NULL;
    size_t fsz = 0;
#ifdef _WIN32
    HANDLE hFile = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               NULL, OPEN_EXISTING,
                               FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (hFile == INVALID_HANDLE_VALUE) return NULL;
    LARGE_INTEGER li;
    if (!GetFileSizeEx(hFile, &li)) { CloseHandle(hFile); return NULL; }
    fsz = (size_t)li.QuadPart;
    if (fsz == 0) { CloseHandle(hFile); return NULL; }
    HANDLE hMap = CreateFileMappingA(hFile, NULL, PAGE_READONLY, 0, 0, NULL);
    if (!hMap) { CloseHandle(hFile); return NULL; }
    filebuf = (uint8_t *)MapViewOfFile(hMap, FILE_MAP_READ, 0, 0, 0);
    if (!filebuf) { CloseHandle(hMap); CloseHandle(hFile); return NULL; }
    /* hFile and hMap kept open until reader is closed */
#else
    int fd = open(path, O_RDONLY);
    if (fd < 0) return NULL;
    struct stat st;
    if (fstat(fd, &st) != 0) { close(fd); return NULL; }
    fsz = (size_t)st.st_size;
    if (fsz == 0) { close(fd); return NULL; }
    filebuf = (uint8_t *)mmap(NULL, fsz, PROT_READ, MAP_PRIVATE, fd, 0);
    if (filebuf == MAP_FAILED) { close(fd); return NULL; }
    close(fd); /* fd can be closed after mmap */
#endif

    /* Validate file header (§4) */
    if (fsz < MEAS_FILE_HEADER_SIZE) { free(filebuf); return NULL; }
    uint32_t magic = read_le32(filebuf);
    if (magic != MEAS_MAGIC) { free(filebuf); return NULL; }
    uint16_t version = read_le16(filebuf + 4);
    if (version != MEAS_VERSION) { free(filebuf); return NULL; }
    uint16_t file_flags = read_le16(filebuf + 6);

    int64_t first_seg_off  = (int64_t)read_le64(filebuf + 8);
    int64_t segment_count_hdr = (int64_t)read_le64(filebuf + 24);

    MeasReader *r = (MeasReader *)calloc(1, sizeof(MeasReader));
    if (!r) {
#ifdef _WIN32
        UnmapViewOfFile(filebuf); CloseHandle(hMap); CloseHandle(hFile);
#else
        munmap(filebuf, fsz);
#endif
        return NULL;
    }
    /* Store mmap handles for cleanup in meas_reader_close() */
#ifdef _WIN32
    r->mmap_file = hFile;
    r->mmap_mapping = hMap;
    r->mmap_base = filebuf;
#else
    r->mmap_base = filebuf;
    r->mmap_size = fsz;
#endif

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
        int32_t seg_flags   = (int32_t)read_le32(shdr + 4);
        int64_t content_len = (int64_t)read_le64(shdr + 8);
        int64_t next_off    = (int64_t)read_le64(shdr + 16);

        size_t content_start = (size_t)offset + MEAS_SEG_HEADER_SIZE;
        if (content_len < 0 || content_start + (size_t)content_len > fsz) break;

        const uint8_t *content = filebuf + content_start;
        size_t         content_sz = (size_t)content_len;

        /* Decompress if segment is compressed (§4a) */
        uint8_t *decompressed = NULL;
        MeasCompression comp = (MeasCompression)(seg_flags & 0x0F);
        if (comp != MEAS_COMPRESS_NONE) {
            size_t dec_len = 0;
            decompressed = decompress_segment(content, content_sz, comp, &dec_len);
            if (!decompressed) { /* skip segment on decompression failure */
                if (next_off <= offset) break;
                offset = next_off;
                continue;
            }
            content = decompressed;
            content_sz = dec_len;
        }

        if (seg_type == MEAS_SEG_TYPE_METADATA) {
            /* Decode metadata first so channels exist for data segments */
            decode_metadata_segment(r, content, content_sz,
                                       (file_flags & MEAS_FLAG_EXTENDED_METADATA) ? 1 : 0);
            /* Build flat channel index */
            total_channels = 0;
            for (int gi = 0; gi < r->group_count; gi++)
                total_channels += r->groups[gi].channel_count;
            flat_channels = (MeasChannelData **)calloc(
                (size_t)(total_channels > 0 ? total_channels : 1), sizeof(*flat_channels));
            if (!flat_channels) { free(decompressed); break; }
            int idx = 0;
            for (int gi = 0; gi < r->group_count; gi++)
                for (int ci = 0; ci < r->groups[gi].channel_count; ci++)
                    flat_channels[idx++] = &r->groups[gi].channels[ci];
        } else if (seg_type == MEAS_SEG_TYPE_DATA && flat_channels) {
            /* Data from mmap (no decompression) is borrowed — zero-copy eligible */
            int data_borrowed = (decompressed == NULL) ? 1 : 0;
            decode_data_segment(content, content_sz, flat_channels, total_channels, data_borrowed);
        }

        free(decompressed);

        if (next_off <= offset) break; /* end of chain (§5) */
        offset = next_off;
    }

    free(flat_channels);
    /* filebuf is memory-mapped — released in meas_reader_close() */
    return r;
}

void meas_reader_close(MeasReader *reader) {
    if (!reader) return;
    for (int gi = 0; gi < reader->group_count; gi++) {
        MeasGroupData *g = &reader->groups[gi];
        for (int ci = 0; ci < g->channel_count; ci++) {
            MeasChannelData *ch = &g->channels[ci];
            free(ch->name);
            if (ch->data_owned) free(ch->data);
            free_properties(ch->properties, ch->property_count);
        }
        free(g->channels);
        free(g->name);
        free_properties(g->properties, g->property_count);
    }
    free(reader->groups);
    free_properties(reader->file_props, reader->file_prop_count);
    /* Release memory mapping */
#ifdef _WIN32
    if (reader->mmap_base) UnmapViewOfFile(reader->mmap_base);
    if (reader->mmap_mapping) CloseHandle(reader->mmap_mapping);
    if (reader->mmap_file) CloseHandle(reader->mmap_file);
#else
    if (reader->mmap_base) munmap(reader->mmap_base, reader->mmap_size);
#endif
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

/* ── File-level property reader API ────────────────────────────────────── */

int meas_reader_file_property_count(const MeasReader *reader) {
    return reader ? reader->file_prop_count : 0;
}

const MeasProperty *meas_reader_file_property(const MeasReader *reader, int idx) {
    if (!reader || idx < 0 || idx >= reader->file_prop_count) return NULL;
    return &reader->file_props[idx];
}

const MeasProperty *meas_reader_file_property_by_name(const MeasReader *reader, const char *key) {
    if (!reader || !key) return NULL;
    for (int i = 0; i < reader->file_prop_count; i++)
        if (strcmp(reader->file_props[i].key, key) == 0)
            return &reader->file_props[i];
    return NULL;
}

/* ── Typed read helpers ──────────────────────────────────────────────────── */

/* For fixed-size LE types: just copy bytes, with possible byte-swap on BE hosts */
/* Generic bulk read: on LE hosts, memcpy directly; on BE hosts, byte-swap each element */
static int64_t read_bulk(const MeasChannelData *ch, void *out,
                          MeasDataType expected, int elem_size, int64_t max_count) {
    if (!ch || ch->data_type != expected || !out) return -1;
    int64_t n = ch->sample_count < max_count ? ch->sample_count : max_count;
#if !MEAS_BIG_ENDIAN
    memcpy(out, ch->data, (size_t)n * (size_t)elem_size);
#else
    if (elem_size == 1) {
        memcpy(out, ch->data, (size_t)n);
    } else {
        for (int64_t i = 0; i < n; i++) {
            const uint8_t *src = ch->data + i * elem_size;
            uint8_t *dst = (uint8_t *)out + i * elem_size;
            for (int b = 0; b < elem_size; b++) dst[b] = src[elem_size - 1 - b];
        }
    }
#endif
    return n;
}

int64_t meas_channel_read_f32(const MeasChannelData *ch, float    *out, int64_t max_count) {
    return read_bulk(ch, out, MEAS_FLOAT32, 4, max_count);
}
int64_t meas_channel_read_f64(const MeasChannelData *ch, double   *out, int64_t max_count) {
    return read_bulk(ch, out, MEAS_FLOAT64, 8, max_count);
}
int64_t meas_channel_read_i8 (const MeasChannelData *ch, int8_t   *out, int64_t max_count) {
    return read_bulk(ch, out, MEAS_INT8,    1, max_count);
}
int64_t meas_channel_read_i16(const MeasChannelData *ch, int16_t  *out, int64_t max_count) {
    return read_bulk(ch, out, MEAS_INT16,   2, max_count);
}
int64_t meas_channel_read_i32(const MeasChannelData *ch, int32_t  *out, int64_t max_count) {
    return read_bulk(ch, out, MEAS_INT32,   4, max_count);
}
int64_t meas_channel_read_i64(const MeasChannelData *ch, int64_t  *out, int64_t max_count) {
    return read_bulk(ch, out, MEAS_INT64,   8, max_count);
}
int64_t meas_channel_read_u8 (const MeasChannelData *ch, uint8_t  *out, int64_t max_count) {
    return read_bulk(ch, out, MEAS_UINT8,   1, max_count);
}
int64_t meas_channel_read_u16(const MeasChannelData *ch, uint16_t *out, int64_t max_count) {
    return read_bulk(ch, out, MEAS_UINT16,  2, max_count);
}
int64_t meas_channel_read_u32(const MeasChannelData *ch, uint32_t *out, int64_t max_count) {
    return read_bulk(ch, out, MEAS_UINT32,  4, max_count);
}
int64_t meas_channel_read_u64(const MeasChannelData *ch, uint64_t *out, int64_t max_count) {
    return read_bulk(ch, out, MEAS_UINT64,  8, max_count);
}
int64_t meas_channel_read_timestamp(const MeasChannelData *ch, int64_t *out_ns, int64_t max_count) {
    if (!ch || (ch->data_type != MEAS_TIMESTAMP && ch->data_type != MEAS_TIMESPAN) || !out_ns)
        return -1;
    int64_t n = ch->sample_count < max_count ? ch->sample_count : max_count;
#if !MEAS_BIG_ENDIAN
    memcpy(out_ns, ch->data, (size_t)n * 8);
#else
    for (int64_t i = 0; i < n; i++) out_ns[i] = (int64_t)read_le64(ch->data + i * 8);
#endif
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
