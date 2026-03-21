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

/* ── DLL export/import macro ────────────────────────────────────────────── */

#if defined(MEASFLOW_SHARED) && (defined(_WIN32) || defined(__CYGWIN__))
  #ifdef MEASFLOW_EXPORTS
    #define MEAS_API __declspec(dllexport)
  #else
    #define MEAS_API __declspec(dllimport)
  #endif
#elif defined(MEASFLOW_SHARED) && defined(__GNUC__)
  #define MEAS_API __attribute__((visibility("default")))
#else
  #define MEAS_API
#endif

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
    int      data_owned; /* 1 if data was malloc'd and must be freed, 0 if borrowed (mmap) */

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
 * Open a .meas file for reading using memory-mapped I/O.
 * The file is mapped read-only; the OS pages in data on demand.
 * @return  Non-NULL handle on success; NULL on error (invalid file, OOM, etc.).
 */
MEAS_API MeasReader *meas_reader_open(const char *path);

/** Free all memory and close the reader. */
MEAS_API void meas_reader_close(MeasReader *reader);

/** Return the number of groups in the file. */
MEAS_API int meas_reader_group_count(const MeasReader *reader);

/**
 * Return the group at the given zero-based index.
 * @return  Pointer to the group (owned by the reader), or NULL if out-of-range.
 */
MEAS_API const MeasGroupData *meas_reader_group(const MeasReader *reader, int group_idx);

/**
 * Find a group by name.
 * @return  Pointer to the group (owned by the reader), or NULL if not found.
 */
MEAS_API const MeasGroupData *meas_reader_group_by_name(const MeasReader *reader, const char *name);

/**
 * Find a channel within a group by name.
 * @return  Pointer to the channel (owned by the reader), or NULL if not found.
 */
MEAS_API const MeasChannelData *meas_group_channel_by_name(const MeasGroupData *group, const char *name);

/* ── Typed read helpers ──────────────────────────────────────────────────────
 * All functions copy up to `max_count` samples from `ch->data` into `out`.
 * @return  Number of samples copied, or -1 on type mismatch.
 */
MEAS_API int64_t meas_channel_read_f32(const MeasChannelData *ch, float    *out, int64_t max_count);
MEAS_API int64_t meas_channel_read_f64(const MeasChannelData *ch, double   *out, int64_t max_count);
MEAS_API int64_t meas_channel_read_i8 (const MeasChannelData *ch, int8_t   *out, int64_t max_count);
MEAS_API int64_t meas_channel_read_i16(const MeasChannelData *ch, int16_t  *out, int64_t max_count);
MEAS_API int64_t meas_channel_read_i32(const MeasChannelData *ch, int32_t  *out, int64_t max_count);
MEAS_API int64_t meas_channel_read_i64(const MeasChannelData *ch, int64_t  *out, int64_t max_count);
MEAS_API int64_t meas_channel_read_u8 (const MeasChannelData *ch, uint8_t  *out, int64_t max_count);
MEAS_API int64_t meas_channel_read_u16(const MeasChannelData *ch, uint16_t *out, int64_t max_count);
MEAS_API int64_t meas_channel_read_u32(const MeasChannelData *ch, uint32_t *out, int64_t max_count);
MEAS_API int64_t meas_channel_read_u64(const MeasChannelData *ch, uint64_t *out, int64_t max_count);

/**
 * Read MEAS_TIMESTAMP or MEAS_TIMESPAN samples as nanosecond int64 values.
 * @return  Number of samples copied, or -1 on type mismatch.
 */
MEAS_API int64_t meas_channel_read_timestamp(const MeasChannelData *ch, int64_t *out_ns, int64_t max_count);

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
MEAS_API int meas_channel_next_frame(const MeasChannelData *ch, int64_t *state,
                                      const uint8_t **frame_data, int32_t *frame_length);

/* ── §10 Bus Metadata types ───────────────────────────────────────────────── */

/** §10.1 Bus type codes. */
typedef enum MeasBusType {
    MEAS_BUS_NONE     = 0,
    MEAS_BUS_CAN      = 1,
    MEAS_BUS_CAN_FD   = 2,
    MEAS_BUS_LIN      = 3,
    MEAS_BUS_FLEXRAY  = 4,
    MEAS_BUS_ETHERNET = 5,
    MEAS_BUS_MOST     = 6,
} MeasBusType;

/** §10.1 Bus-level configuration. */
typedef struct MeasBusConfig {
    MeasBusType bus_type;
    union {
        struct { int     is_extended_id; int32_t baud_rate; }                       can;
        struct { int     is_extended_id; int32_t arb_baud_rate; int32_t data_baud_rate; } can_fd;
        struct { int32_t baud_rate;      uint8_t lin_version; }                     lin;
        struct { int32_t cycle_time_us;  int32_t macroticks_per_cycle; }            flexray;
        /* ethernet and most: no extra fields */
    } u;
} MeasBusConfig;

/** §10.4 Multiplex condition (recursive — parent is heap-allocated). */
typedef struct MeasMultiplexCondition {
    char                        *multiplexer_signal_name;
    int64_t                      low_value;   /* inclusive lower bound */
    int64_t                      high_value;  /* inclusive upper bound */
    int                          has_parent;
    struct MeasMultiplexCondition *parent;    /* heap-allocated; NULL when !has_parent */
} MeasMultiplexCondition;

/** Value description entry inside a SignalDefinition. */
typedef struct MeasValueDescription {
    int64_t  value;
    char    *description;
} MeasValueDescription;

/**
 * §10.3 Signal definition.
 * byte_order: 0 = Intel/LE, 1 = Motorola/BE.
 * signal_type: 0 = Unsigned, 1 = Signed, 2 = Float32, 3 = Float64.
 */
typedef struct MeasSignalDefinition {
    char    *name;
    int32_t  start_bit;
    int32_t  bit_length;
    uint8_t  byte_order;
    uint8_t  signal_type;
    double   factor;
    double   offset;
    uint8_t  min_max_flags; /* bit 0: hasMin, bit 1: hasMax */
    double   min_value;     /* present if bit 0 set */
    double   max_value;     /* present if bit 1 set */
    int      has_unit;
    char    *unit;          /* present if has_unit */
    int      is_multiplexer;
    int      has_multiplex_condition;
    MeasMultiplexCondition *multiplex_condition; /* heap-allocated; NULL when !has_multiplex_condition */
    int32_t              value_desc_count;
    MeasValueDescription *value_descs;
} MeasSignalDefinition;

/** §10.6 E2E protection configuration. */
typedef struct MeasE2EProtection {
    uint8_t  profile;
    int32_t  crc_start_bit;
    int32_t  crc_bit_length;
    int32_t  counter_start_bit;
    int32_t  counter_bit_length;
    uint32_t data_id;
    uint32_t crc_polynomial;
} MeasE2EProtection;

/** §10.7 SecOC (Secure Onboard Communication) configuration. */
typedef struct MeasSecOcConfig {
    uint8_t  algorithm;           /* 0=CmacAes128, 1=CmacAes256, 2=HmacSha256, 3=HmacSha384 */
    int32_t  freshness_start_bit;
    int32_t  freshness_truncated_length;
    int32_t  freshness_full_length;
    uint8_t  freshness_type;      /* 0=Counter, 1=Timestamp, 2=Both */
    int32_t  mac_start_bit;
    int32_t  mac_truncated_length;
    int32_t  mac_full_length;
    int32_t  authen_payload_length;
    uint32_t data_id;
    int32_t  auth_build_attempts;
    int      use_freshness_value_manager;
    uint32_t key_id;
} MeasSecOcConfig;

/** Single mux group inside a MultiplexConfig. */
typedef struct MeasMuxGroup {
    int64_t  mux_value;
    int32_t  signal_name_count;
    char   **signal_names; /* array of heap-allocated strings */
} MeasMuxGroup;

/** §10.8 Multiplex configuration. */
typedef struct MeasMultiplexConfig {
    char        *multiplexer_signal_name;
    int32_t      group_count;
    MeasMuxGroup *groups;
} MeasMultiplexConfig;

/** §10.9 Contained PDU (AUTOSAR I-PDU multiplexing). */
typedef struct MeasContainedPdu {
    char                *name;
    uint32_t             header_id;
    int32_t              length;
    int32_t              signal_count;
    MeasSignalDefinition *signals;
} MeasContainedPdu;

/** §10.5 PDU definition. */
typedef struct MeasPduDefinition {
    char    *name;
    uint32_t pdu_id;
    int32_t  byte_offset;
    int32_t  length;
    int      is_container_pdu;
    int      has_e2e;
    MeasE2EProtection   *e2e;     /* heap-allocated; NULL when !has_e2e */
    int      has_secoc;
    MeasSecOcConfig     *secoc;   /* heap-allocated; NULL when !has_secoc */
    int      has_multiplexing;
    MeasMultiplexConfig *multiplex; /* heap-allocated; NULL when !has_multiplexing */
    int32_t              signal_count;
    MeasSignalDefinition *signals;
    int32_t              contained_pdu_count;
    MeasContainedPdu    *contained_pdus;
} MeasPduDefinition;

/**
 * §10.2 Frame definition.
 * direction: 0=Rx, 1=Tx, 2=TxRq.
 * flags: Error=1, Remote=2, WakeUp=4, SingleShot=8.
 * The `bus` union member is selected by the parent BusMetadata's bus type.
 */
typedef struct MeasFrameDefinition {
    char    *name;
    uint32_t frame_id;
    int32_t  payload_length;
    uint8_t  direction;
    uint16_t flags;
    union {
        struct { int is_extended_id; }                                               can;
        struct { int is_extended_id; int bit_rate_switch; int error_state_indicator; } can_fd;
        struct { uint8_t nad; uint8_t checksum_type; }                               lin;
        struct { uint8_t cycle_count; uint8_t channel; }                             flexray;
        struct { uint8_t mac_source[6]; uint8_t mac_dest[6];
                 uint16_t vlan_id; uint16_t ether_type; }                            ethernet;
        struct { uint16_t function_block; uint8_t instance_id;
                 uint16_t function_id; }                                             most;
    } bus;
    int32_t               signal_count;
    MeasSignalDefinition *signals;
    int32_t               pdu_count;
    MeasPduDefinition    *pdus;
} MeasFrameDefinition;

/** §10.10 Single entry in a value table. */
typedef struct MeasValueTableEntry {
    int64_t  value;
    char    *description;
} MeasValueTableEntry;

/** §10.10 Value table (raw-value-to-text mappings). */
typedef struct MeasValueTable {
    char                *name;
    int32_t              entry_count;
    MeasValueTableEntry *entries;
} MeasValueTable;

/**
 * §10 Top-level bus metadata blob, stored as the `MEAS.bus_def` group property.
 * format_version is currently 1.
 */
typedef struct MeasBusMetadata {
    uint8_t              format_version;
    MeasBusConfig        bus_config;
    char                *raw_frame_channel_name;
    char                *timestamp_channel_name;
    int32_t              frame_count;
    MeasFrameDefinition *frames;
    int32_t              value_table_count;
    MeasValueTable      *value_tables;
} MeasBusMetadata;

/* ── §11 Raw Frame wire-format types ──────────────────────────────────────── */

/**
 * §11 CAN / CAN-FD wire frame.
 * flags: bit 0 = BRS, bit 1 = ESI, bit 2 = ExtendedId.
 * dlc must be ≤ MEAS_CAN_PAYLOAD_MAX (64 bytes for CAN-FD).
 */
#define MEAS_CAN_PAYLOAD_MAX 64
typedef struct MeasCanFrame {
    uint32_t arb_id;
    uint8_t  dlc;
    uint8_t  flags;
    uint8_t  payload[MEAS_CAN_PAYLOAD_MAX];
} MeasCanFrame;

/**
 * §11 LIN wire frame.
 * dlc must be ≤ MEAS_LIN_PAYLOAD_MAX (8 bytes).
 */
#define MEAS_LIN_PAYLOAD_MAX 8
typedef struct MeasLinFrame {
    uint8_t frame_id;
    uint8_t dlc;
    uint8_t nad;
    uint8_t checksum_type;
    uint8_t payload[MEAS_LIN_PAYLOAD_MAX];
} MeasLinFrame;

/**
 * §11 FlexRay wire frame.
 * payload points into the channel data buffer (zero-copy) — valid while the
 * reader is open, or into caller-supplied memory for writes.
 * channel_flags: bit 0 = ChA, bit 1 = ChB.
 */
typedef struct MeasFlexRayFrame {
    uint16_t        slot_id;
    uint8_t         cycle_count;
    uint8_t         channel_flags;
    uint16_t        payload_length;
    const uint8_t  *payload;  /* NOT owned */
} MeasFlexRayFrame;

/**
 * §11 Ethernet wire frame.
 * payload points into the channel data buffer (zero-copy) — valid while the
 * reader is open, or into caller-supplied memory for writes.
 */
typedef struct MeasEthernetFrame {
    uint8_t         mac_dest[6];
    uint8_t         mac_src[6];
    uint16_t        ether_type;
    uint16_t        vlan_id;
    uint16_t        payload_length;
    const uint8_t  *payload;  /* NOT owned */
} MeasEthernetFrame;

/* ── Compression (§4a) ────────────────────────────────────────────────────── */

typedef enum MeasCompression {
    MEAS_COMPRESS_NONE = 0,
    MEAS_COMPRESS_LZ4  = 1,
    MEAS_COMPRESS_ZSTD = 2,
} MeasCompression;

/* ── Writer types ──────────────────────────────────────────────────────────── */

typedef struct MeasWriter        MeasWriter;
typedef struct MeasGroupWriter   MeasGroupWriter;
typedef struct MeasChannelWriter MeasChannelWriter;

/* ── File Header Flags (§4a-flags) ─────────────────────────────────────────── */

#define MEAS_FLAG_EXTENDED_METADATA    0x0001
#define MEAS_FLAG_HAS_FILE_PROPERTIES  MEAS_FLAG_EXTENDED_METADATA  /* legacy alias */

/* ── Writer API ────────────────────────────────────────────────────────────── */

/**
 * Create a new .meas file for writing.
 * @return  Non-NULL writer handle on success; NULL on error.
 */
MEAS_API MeasWriter *meas_writer_open(const char *path);

/**
 * Set compression algorithm for data segments.
 * Must be called before the first flush. Default is MEAS_COMPRESS_NONE.
 * Requires the library to be built with MEAS_HAVE_LZ4 / MEAS_HAVE_ZSTD.
 * @return  0 on success, -1 if compression is not available.
 */
MEAS_API int meas_writer_set_compression(MeasWriter *writer, MeasCompression compression);

/**
 * Flush all buffered samples to the file as a new Data segment.
 * May be called multiple times to produce a multi-segment (streaming) file.
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_writer_flush(MeasWriter *writer);

/**
 * Flush remaining data, finalise the file header, and close the file.
 * The writer pointer is invalid after this call.
 */
MEAS_API void meas_writer_close(MeasWriter *writer);

/**
 * Set a file-level string property. Must be called before first flush.
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_writer_set_property_str(MeasWriter *writer, const char *key, const char *value);

/**
 * Set a file-level int32 property. Must be called before first flush.
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_writer_set_property_i32(MeasWriter *writer, const char *key, int32_t value);

/**
 * Set a file-level float64 property. Must be called before first flush.
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_writer_set_property_f64(MeasWriter *writer, const char *key, double value);

/**
 * Get file-level property count from the reader.
 */
MEAS_API int meas_reader_file_property_count(const MeasReader *reader);

/**
 * Get a file-level property by index (0-based).
 * @return  Pointer to the property (owned by the reader), or NULL.
 */
MEAS_API const MeasProperty *meas_reader_file_property(const MeasReader *reader, int idx);

/**
 * Find a file-level property by key.
 * @return  Pointer to the property (owned by the reader), or NULL.
 */
MEAS_API const MeasProperty *meas_reader_file_property_by_name(const MeasReader *reader, const char *key);

/**
 * Add a named group to the writer.
 * Must be called before any data is written (i.e., before the first flush).
 * @return  Non-NULL group handle on success; NULL on error.
 */
MEAS_API MeasGroupWriter *meas_writer_add_group(MeasWriter *writer, const char *name);

/**
 * Add a named channel to a group.
 * @param dtype  One of the MEAS_* data type constants.
 * @return  Non-NULL channel handle on success; NULL on error.
 */
MEAS_API MeasChannelWriter *meas_group_add_channel(MeasGroupWriter *group, const char *name,
                                                    MeasDataType dtype);

/**
 * Enable or disable automatic statistics tracking for a channel.
 * By default, statistics (min, max, mean, std-dev, …) are tracked for
 * numeric types.  Disabling can improve write performance when statistics
 * are not needed.  Must be called before the first write.
 * @param enable  Non-zero to enable (default), zero to disable.
 */
MEAS_API void meas_channel_set_statistics(MeasChannelWriter *ch, int enable);

/* ── Typed write helpers ─────────────────────────────────────────────────────
 * Array variants append `count` samples from `data[0..count-1]`.
 * Single-value variants append exactly one sample.
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_channel_write_f32 (MeasChannelWriter *ch, const float    *data, int64_t count);
MEAS_API int meas_channel_write_f64 (MeasChannelWriter *ch, const double   *data, int64_t count);
MEAS_API int meas_channel_write_i8  (MeasChannelWriter *ch, const int8_t   *data, int64_t count);
MEAS_API int meas_channel_write_i16 (MeasChannelWriter *ch, const int16_t  *data, int64_t count);
MEAS_API int meas_channel_write_i32 (MeasChannelWriter *ch, const int32_t  *data, int64_t count);
MEAS_API int meas_channel_write_i64 (MeasChannelWriter *ch, const int64_t  *data, int64_t count);
MEAS_API int meas_channel_write_u8  (MeasChannelWriter *ch, const uint8_t  *data, int64_t count);
MEAS_API int meas_channel_write_u16 (MeasChannelWriter *ch, const uint16_t *data, int64_t count);
MEAS_API int meas_channel_write_u32 (MeasChannelWriter *ch, const uint32_t *data, int64_t count);
MEAS_API int meas_channel_write_u64 (MeasChannelWriter *ch, const uint64_t *data, int64_t count);

/** Write MEAS_TIMESTAMP / MEAS_TIMESPAN values (nanoseconds as int64). */
MEAS_API int meas_channel_write_timestamp(MeasChannelWriter *ch, const int64_t *ns_values, int64_t count);

/* Single-sample convenience wrappers */
MEAS_API int meas_channel_write_f32_one(MeasChannelWriter *ch, float    value);
MEAS_API int meas_channel_write_f64_one(MeasChannelWriter *ch, double   value);
MEAS_API int meas_channel_write_i32_one(MeasChannelWriter *ch, int32_t  value);
MEAS_API int meas_channel_write_i64_one(MeasChannelWriter *ch, int64_t  value);
MEAS_API int meas_channel_write_bool_one(MeasChannelWriter *ch, int value);

/**
 * Write a single variable-length binary frame (MEAS_BINARY channel).
 * Stores [int32: length][bytes] per the §7 wire format.
 */
MEAS_API int meas_channel_write_frame(MeasChannelWriter *ch, const uint8_t *frame, int32_t length);

/**
 * Write a single UTF-8 string sample to a MEAS_STRING channel (§7).
 * The string is stored as [int32: byteLength][bytes: UTF-8 data] without a
 * null terminator.  `str` must be a null-terminated, valid UTF-8 C string.
 * UTF-8 validity is not checked; the caller is responsible for encoding.
 * Strings longer than INT32_MAX bytes are rejected with a return value of -1.
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_channel_write_string(MeasChannelWriter *ch, const char *str);

/* ── §10 Bus Metadata API ─────────────────────────────────────────────────── */

/**
 * Encode a MeasBusMetadata struct into a newly-allocated byte array.
 * On success, *out_data is heap-allocated (caller must free()) and *out_len
 * is set to the byte count.
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_bus_metadata_encode(const MeasBusMetadata *meta,
                                       uint8_t **out_data, int32_t *out_len);

/**
 * Decode a MeasBusMetadata blob (as stored in the `MEAS.bus_def` property).
 * *out_meta is heap-allocated; free with meas_bus_metadata_free().
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_bus_metadata_decode(const uint8_t *data, int32_t len,
                                       MeasBusMetadata **out_meta);

/**
 * Free a MeasBusMetadata returned by meas_bus_metadata_decode().
 * Also accepts NULL (no-op).
 */
MEAS_API void meas_bus_metadata_free(MeasBusMetadata *meta);

/**
 * Set group property `MEAS.bus_def` by encoding `meta` as a binary blob.
 * Must be called before the first flush.
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_group_set_bus_def(MeasGroupWriter *group, const MeasBusMetadata *meta);

/**
 * Set an arbitrary binary property on a group writer.
 * Must be called before the first flush.
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_group_set_property_bin(MeasGroupWriter *group, const char *key,
                                          const uint8_t *data, int32_t len);

/**
 * Set a string property on a group writer.
 * Must be called before the first flush.
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_group_set_property_str(MeasGroupWriter *group, const char *key, const char *value);

/**
 * Set an int32 property on a group writer.
 * Must be called before the first flush.
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_group_set_property_i32(MeasGroupWriter *group, const char *key, int32_t value);

/**
 * Set a float64 property on a group writer.
 * Must be called before the first flush.
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_group_set_property_f64(MeasGroupWriter *group, const char *key, double value);

/**
 * Set a string property on a channel writer.
 * Must be called before the first flush.
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_channel_set_property_str(MeasChannelWriter *ch, const char *key, const char *value);

/**
 * Set an int32 property on a channel writer.
 * Must be called before the first flush.
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_channel_set_property_i32(MeasChannelWriter *ch, const char *key, int32_t value);

/**
 * Set a float64 property on a channel writer.
 * Must be called before the first flush.
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_channel_set_property_f64(MeasChannelWriter *ch, const char *key, double value);

/**
 * Find the `MEAS.bus_def` binary property in a group and decode it.
 * *out_meta is heap-allocated; free with meas_bus_metadata_free().
 * @return  0 on success, -1 if property is absent or decoding fails.
 */
MEAS_API int meas_group_read_bus_def(const MeasGroupData *group, MeasBusMetadata **out_meta);

/* ── §11 Typed frame write helpers ───────────────────────────────────────── */

/**
 * Write a single CAN or CAN-FD frame to a MEAS_BINARY channel.
 * Wire format: [uint32: arb_id][byte: dlc][byte: flags][payload: dlc bytes].
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_channel_write_can_frame(MeasChannelWriter *ch, const MeasCanFrame *frame);

/**
 * Write a single LIN frame to a MEAS_BINARY channel.
 * Wire format: [byte: frame_id][byte: dlc][byte: nad][byte: checksum_type][payload: dlc bytes].
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_channel_write_lin_frame(MeasChannelWriter *ch, const MeasLinFrame *frame);

/**
 * Write a single FlexRay frame to a MEAS_BINARY channel.
 * Wire format: [uint16: slot_id][byte: cycle_count][byte: channel_flags]
 *              [uint16: payload_length][payload: payload_length bytes].
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_channel_write_flexray_frame(MeasChannelWriter *ch, const MeasFlexRayFrame *frame);

/**
 * Write a single Ethernet frame to a MEAS_BINARY channel.
 * Wire format: [6B: mac_dest][6B: mac_src][uint16: ether_type][uint16: vlan_id]
 *              [uint16: payload_length][payload: payload_length bytes].
 * @return  0 on success, -1 on error.
 */
MEAS_API int meas_channel_write_ethernet_frame(MeasChannelWriter *ch, const MeasEthernetFrame *frame);

/* ── §11 Typed frame read helpers ────────────────────────────────────────── */

/**
 * Decode the next CAN / CAN-FD frame from a MEAS_BINARY channel.
 * Copies frame fields (including payload bytes) into *out.
 * Call with *state = 0 to start; advances *state on each call.
 * @return  1 while frames remain, 0 when exhausted, -1 on error.
 */
MEAS_API int meas_channel_next_can_frame(const MeasChannelData *ch, int64_t *state,
                                          MeasCanFrame *out);

/**
 * Decode the next LIN frame from a MEAS_BINARY channel.
 * @return  1 while frames remain, 0 when exhausted, -1 on error.
 */
MEAS_API int meas_channel_next_lin_frame(const MeasChannelData *ch, int64_t *state,
                                          MeasLinFrame *out);

/**
 * Decode the next FlexRay frame from a MEAS_BINARY channel.
 * out->payload points into ch->data (zero-copy); valid while the reader is open.
 * @return  1 while frames remain, 0 when exhausted, -1 on error.
 */
MEAS_API int meas_channel_next_flexray_frame(const MeasChannelData *ch, int64_t *state,
                                              MeasFlexRayFrame *out);

/**
 * Decode the next Ethernet frame from a MEAS_BINARY channel.
 * out->payload points into ch->data (zero-copy); valid while the reader is open.
 * @return  1 while frames remain, 0 when exhausted, -1 on error.
 */
MEAS_API int meas_channel_next_ethernet_frame(const MeasChannelData *ch, int64_t *state,
                                               MeasEthernetFrame *out);

#ifdef __cplusplus
}
#endif

#endif /* MEASFLOW_H */
