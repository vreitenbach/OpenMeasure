/**
 * measflow.h — C Reader/Writer for the MeasFlow (.meas) binary format
 *
 * Version 1.0  |  MIT License
 *
 * Conforms to the MeasFlow binary format specification v1.
 * All values are stored in little-endian byte order.
 *
 * Usage — Writer:
 *   MeasWriter *w = meas_writer_open("out.meas");
 *   MeasGroupWriter *g = meas_writer_add_group(w, "Sensors");
 *   MeasChannelWriter *ch = meas_group_add_channel(g, "Temperature", MEAS_FLOAT64);
 *   meas_channel_write_f64(ch, 23.5);
 *   meas_writer_close(w);   // flushes and finalises
 *
 * Usage — Reader:
 *   MeasReader *r = meas_reader_open("out.meas");
 *   const MeasGroupData *grp = meas_reader_group_by_name(r, "Sensors");
 *   const MeasChannelData *ch = meas_group_channel_by_name(grp, "Temperature");
 *   double *buf = malloc(ch->sample_count * sizeof(double));
 *   meas_channel_read_f64(ch, buf, ch->sample_count);
 *   free(buf);
 *   meas_reader_close(r);
 */

#ifndef MEASFLOW_H
#define MEASFLOW_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ── Data type codes (§8) ─────────────────────────────────────────────────── */

typedef enum MeasDataType {
    MEAS_INT8      = 0x01,
    MEAS_INT16     = 0x02,
    MEAS_INT32     = 0x03,
    MEAS_INT64     = 0x04,
    MEAS_UINT8     = 0x05,
    MEAS_UINT16    = 0x06,
    MEAS_UINT32    = 0x07,
    MEAS_UINT64    = 0x08,
    MEAS_FLOAT32   = 0x10,
    MEAS_FLOAT64   = 0x11,
    MEAS_TIMESTAMP = 0x20,  /* int64 nanoseconds since Unix epoch */
    MEAS_TIMESPAN  = 0x21,  /* int64 duration in nanoseconds */
    MEAS_STRING    = 0x30,  /* length-prefixed UTF-8 */
    MEAS_BINARY    = 0x31,  /* length-prefixed byte array */
    MEAS_BOOL      = 0x50,  /* 0x00 = false, non-zero = true */
} MeasDataType;

/* ── Property (§9) ─────────────────────────────────────────────────────────── */

/**
 * A typed key-value property attached to a group or channel.
 * String and binary values point into memory owned by the reader/writer;
 * do not free them directly.
 */
typedef struct MeasProperty {
    char        *key;
    MeasDataType type;
    union {
        int8_t   i8;
        int16_t  i16;
        int32_t  i32;
        int64_t  i64;
        uint8_t  u8;
        uint16_t u16;
        uint32_t u32;
        uint64_t u64;
        float    f32;
        double   f64;
        /* MEAS_TIMESTAMP / MEAS_TIMESPAN */
        int64_t  timestamp_ns;
        uint8_t  bool_val;
        struct { char    *data; int32_t length; } str;
        struct { uint8_t *data; int32_t length; } bin;
    } value;
} MeasProperty;

/* ── Channel statistics (§13) ─────────────────────────────────────────────── */

typedef struct MeasChannelStats {
    int64_t count;
    double  min;
    double  max;
    double  sum;
    double  mean;
    double  variance;
    double  first;
    double  last;
} MeasChannelStats;

/* ── Reader types ──────────────────────────────────────────────────────────── */

/**
 * A decoded channel with its full sample data.
 * `data` is a flat byte array of all samples concatenated across segments.
 * For fixed-size types: data_size == sample_count * sizeof(element_type).
 * For MEAS_BINARY / MEAS_STRING: data holds the raw framed bytes
 *   ([int32 len][bytes] per sample); use meas_channel_read_frames() to iterate.
 */
typedef struct MeasChannelData {
    char        *name;
    MeasDataType data_type;

    int           property_count;
    MeasProperty *properties;

    int64_t  sample_count;
    uint8_t *data;       /* raw bytes, NULL if no samples */
    int64_t  data_size;  /* total bytes in data[] */

    int              has_stats;
    MeasChannelStats stats;
} MeasChannelData;

/** A decoded group containing one or more channels. */
typedef struct MeasGroupData {
    char        *name;
    int           property_count;
    MeasProperty *properties;

    int              channel_count;
    MeasChannelData *channels;
} MeasGroupData;

/**
 * An open .meas reader.  Obtain via meas_reader_open().
 * Dispose with meas_reader_close() when done.
 */
typedef struct MeasReader MeasReader;

/* ── Reader API ────────────────────────────────────────────────────────────── */

/**
 * Open a .meas file for reading.  Reads the entire file into memory and
 * parses all segments.
 * @return  Non-NULL handle on success; NULL on error (invalid file, OOM, etc.).
 */
MeasReader *meas_reader_open(const char *path);

/** Free all memory and close the reader. */
void meas_reader_close(MeasReader *reader);

/** Return the number of groups in the file. */
int meas_reader_group_count(const MeasReader *reader);

/**
 * Return the group at the given zero-based index.
 * @return  Pointer to the group (owned by the reader), or NULL if out-of-range.
 */
const MeasGroupData *meas_reader_group(const MeasReader *reader, int group_idx);

/**
 * Find a group by name.
 * @return  Pointer to the group (owned by the reader), or NULL if not found.
 */
const MeasGroupData *meas_reader_group_by_name(const MeasReader *reader, const char *name);

/**
 * Find a channel within a group by name.
 * @return  Pointer to the channel (owned by the reader), or NULL if not found.
 */
const MeasChannelData *meas_group_channel_by_name(const MeasGroupData *group, const char *name);

/* ── Typed read helpers ──────────────────────────────────────────────────────
 * All functions copy up to `max_count` samples from `ch->data` into `out`.
 * @return  Number of samples copied, or -1 on type mismatch.
 */
int64_t meas_channel_read_f32(const MeasChannelData *ch, float    *out, int64_t max_count);
int64_t meas_channel_read_f64(const MeasChannelData *ch, double   *out, int64_t max_count);
int64_t meas_channel_read_i8 (const MeasChannelData *ch, int8_t   *out, int64_t max_count);
int64_t meas_channel_read_i16(const MeasChannelData *ch, int16_t  *out, int64_t max_count);
int64_t meas_channel_read_i32(const MeasChannelData *ch, int32_t  *out, int64_t max_count);
int64_t meas_channel_read_i64(const MeasChannelData *ch, int64_t  *out, int64_t max_count);
int64_t meas_channel_read_u8 (const MeasChannelData *ch, uint8_t  *out, int64_t max_count);
int64_t meas_channel_read_u16(const MeasChannelData *ch, uint16_t *out, int64_t max_count);
int64_t meas_channel_read_u32(const MeasChannelData *ch, uint32_t *out, int64_t max_count);
int64_t meas_channel_read_u64(const MeasChannelData *ch, uint64_t *out, int64_t max_count);

/**
 * Read MEAS_TIMESTAMP or MEAS_TIMESPAN samples as nanosecond int64 values.
 * @return  Number of samples copied, or -1 on type mismatch.
 */
int64_t meas_channel_read_timestamp(const MeasChannelData *ch, int64_t *out_ns, int64_t max_count);

/**
 * Iterate over variable-length frames in a MEAS_BINARY or MEAS_STRING channel.
 * Call repeatedly with *state = 0 to start.  Each call advances *state and
 * stores the next frame into *frame_data / *frame_length.
 * @return  1 while more frames are available, 0 when exhausted, -1 on error.
 *
 * Example:
 *   int64_t state = 0;
 *   const uint8_t *frame; int32_t len;
 *   while (meas_channel_next_frame(ch, &state, &frame, &len) == 1) { ... }
 */
int meas_channel_next_frame(const MeasChannelData *ch, int64_t *state,
                             const uint8_t **frame_data, int32_t *frame_length);

/* ── Writer types ──────────────────────────────────────────────────────────── */

typedef struct MeasWriter        MeasWriter;
typedef struct MeasGroupWriter   MeasGroupWriter;
typedef struct MeasChannelWriter MeasChannelWriter;

/* ── Writer API ────────────────────────────────────────────────────────────── */

/**
 * Create a new .meas file for writing.
 * @return  Non-NULL writer handle on success; NULL on error.
 */
MeasWriter *meas_writer_open(const char *path);

/**
 * Flush all buffered samples to the file as a new Data segment.
 * May be called multiple times to produce a multi-segment (streaming) file.
 * @return  0 on success, -1 on error.
 */
int meas_writer_flush(MeasWriter *writer);

/**
 * Flush remaining data, finalise the file header, and close the file.
 * The writer pointer is invalid after this call.
 */
void meas_writer_close(MeasWriter *writer);

/**
 * Add a named group to the writer.
 * Must be called before any data is written (i.e., before the first flush).
 * @return  Non-NULL group handle on success; NULL on error.
 */
MeasGroupWriter *meas_writer_add_group(MeasWriter *writer, const char *name);

/**
 * Add a named channel to a group.
 * @param dtype  One of the MEAS_* data type constants.
 * @return  Non-NULL channel handle on success; NULL on error.
 */
MeasChannelWriter *meas_group_add_channel(MeasGroupWriter *group, const char *name,
                                           MeasDataType dtype);

/* ── Typed write helpers ─────────────────────────────────────────────────────
 * Array variants append `count` samples from `data[0..count-1]`.
 * Single-value variants append exactly one sample.
 * @return  0 on success, -1 on error.
 */
int meas_channel_write_f32 (MeasChannelWriter *ch, const float    *data, int64_t count);
int meas_channel_write_f64 (MeasChannelWriter *ch, const double   *data, int64_t count);
int meas_channel_write_i8  (MeasChannelWriter *ch, const int8_t   *data, int64_t count);
int meas_channel_write_i16 (MeasChannelWriter *ch, const int16_t  *data, int64_t count);
int meas_channel_write_i32 (MeasChannelWriter *ch, const int32_t  *data, int64_t count);
int meas_channel_write_i64 (MeasChannelWriter *ch, const int64_t  *data, int64_t count);
int meas_channel_write_u8  (MeasChannelWriter *ch, const uint8_t  *data, int64_t count);
int meas_channel_write_u16 (MeasChannelWriter *ch, const uint16_t *data, int64_t count);
int meas_channel_write_u32 (MeasChannelWriter *ch, const uint32_t *data, int64_t count);
int meas_channel_write_u64 (MeasChannelWriter *ch, const uint64_t *data, int64_t count);

/** Write MEAS_TIMESTAMP / MEAS_TIMESPAN values (nanoseconds as int64). */
int meas_channel_write_timestamp(MeasChannelWriter *ch, const int64_t *ns_values, int64_t count);

/* Single-sample convenience wrappers */
int meas_channel_write_f32_one(MeasChannelWriter *ch, float    value);
int meas_channel_write_f64_one(MeasChannelWriter *ch, double   value);
int meas_channel_write_i32_one(MeasChannelWriter *ch, int32_t  value);
int meas_channel_write_i64_one(MeasChannelWriter *ch, int64_t  value);
int meas_channel_write_bool_one(MeasChannelWriter *ch, int value);

/**
 * Write a single variable-length binary frame (MEAS_BINARY channel).
 * Stores [int32: length][bytes] per the §7 wire format.
 */
int meas_channel_write_frame(MeasChannelWriter *ch, const uint8_t *frame, int32_t length);

/**
 * Write a single UTF-8 string sample to a MEAS_STRING channel (§7).
 * The string is stored as [int32: byteLength][bytes: UTF-8 data] without a
 * null terminator.  `str` must be a null-terminated, valid UTF-8 C string.
 * UTF-8 validity is not checked; the caller is responsible for encoding.
 * Strings longer than INT32_MAX bytes are rejected with a return value of -1.
 * @return  0 on success, -1 on error.
 */
int meas_channel_write_string(MeasChannelWriter *ch, const char *str);

#ifdef __cplusplus
}
#endif

#endif /* MEASFLOW_H */
