/**
 * test_measflow.c — Unit tests for the MeasFlow C reader/writer.
 *
 * Compile with CMake (see c/CMakeLists.txt) or directly:
 *   cc -std=c99 -I.. -o test_measflow test_measflow.c ../measflow.c && ./test_measflow
 */

#include "measflow.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ── Minimal test framework ─────────────────────────────────────────────── */

static int  g_tests  = 0;
static int  g_passed = 0;
static int  g_failed = 0;
static const char *g_current_test = "";

#define TEST(name)  do { g_current_test = (name); g_tests++; } while(0)
#define PASS()      do { g_passed++;               \
    printf("  [PASS] %s\n", g_current_test); } while(0)
#define FAIL(msg)   do { g_failed++;               \
    printf("  [FAIL] %s: %s\n", g_current_test, msg); } while(0)

#define ASSERT(cond)  do { if (!(cond)) { \
    FAIL(#cond); return; } } while(0)
#define ASSERT_EQ_INT(a, b) do { if ((a) != (b)) { \
    char _buf[128]; snprintf(_buf,sizeof(_buf),"expected %lld got %lld",(long long)(b),(long long)(a)); \
    FAIL(_buf); return; } } while(0)
#define ASSERT_NEAR(a, b, tol) do { \
    double _diff = (double)(a) - (double)(b); \
    if (_diff < 0) _diff = -_diff; \
    if (_diff > (tol)) { \
        char _buf[128]; snprintf(_buf,sizeof(_buf),"%.6g vs %.6g (tol %.6g)",(double)(a),(double)(b),(double)(tol)); \
        FAIL(_buf); return; } } while(0)

/* ── Temp file helper ────────────────────────────────────────────────────── */

static char g_tmp_path[512];

static const char *tmp_file(const char *name) {
#if defined(_WIN32)
    const char *dir = getenv("TEMP");
    if (!dir) dir = "C:\\Temp";
#else
    const char *dir = "/tmp";
#endif
    snprintf(g_tmp_path, sizeof(g_tmp_path), "%s/meas_test_%s", dir, name);
    return g_tmp_path;
}

/* ── Test cases ──────────────────────────────────────────────────────────── */

static void test_write_and_read_float64(void) {
    TEST("write_and_read_float64");
    const char *path = tmp_file("float64.meas");

    double input[] = {1.0, 2.5, -3.14, 0.0, 1e6};
    int N = 5;

    /* Write */
    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    MeasGroupWriter *g = meas_writer_add_group(w, "Sensors");
    ASSERT(g != NULL);
    MeasChannelWriter *ch = meas_group_add_channel(g, "Voltage", MEAS_FLOAT64);
    ASSERT(ch != NULL);
    ASSERT_EQ_INT(meas_channel_write_f64(ch, input, N), 0);
    meas_writer_close(w);

    /* Read */
    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    ASSERT_EQ_INT(meas_reader_group_count(r), 1);

    const MeasGroupData *grp = meas_reader_group_by_name(r, "Sensors");
    ASSERT(grp != NULL);
    ASSERT(strcmp(grp->name, "Sensors") == 0);
    ASSERT_EQ_INT(grp->channel_count, 1);

    const MeasChannelData *rch = meas_group_channel_by_name(grp, "Voltage");
    ASSERT(rch != NULL);
    ASSERT_EQ_INT(rch->data_type, MEAS_FLOAT64);
    ASSERT_EQ_INT(rch->sample_count, N);

    double out[5];
    int64_t n = meas_channel_read_f64(rch, out, N);
    ASSERT_EQ_INT(n, N);
    for (int i = 0; i < N; i++) ASSERT_NEAR(out[i], input[i], 1e-12);

    meas_reader_close(r);
    PASS();
}

static void test_write_and_read_float32(void) {
    TEST("write_and_read_float32");
    const char *path = tmp_file("float32.meas");

    float input[] = {1.0f, -2.5f, 3.14f, 0.0f};
    int N = 4;

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    MeasGroupWriter *g = meas_writer_add_group(w, "G");
    MeasChannelWriter *ch = meas_group_add_channel(g, "RPM", MEAS_FLOAT32);
    ASSERT_EQ_INT(meas_channel_write_f32(ch, input, N), 0);
    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasGroupData *grp = meas_reader_group(r, 0);
    ASSERT(grp != NULL);
    const MeasChannelData *rch = meas_group_channel_by_name(grp, "RPM");
    ASSERT(rch != NULL);
    ASSERT_EQ_INT(rch->data_type, MEAS_FLOAT32);
    ASSERT_EQ_INT(rch->sample_count, N);

    float out[4];
    ASSERT_EQ_INT(meas_channel_read_f32(rch, out, N), N);
    for (int i = 0; i < N; i++) ASSERT_NEAR(out[i], input[i], 1e-5f);

    meas_reader_close(r);
    PASS();
}

static void test_write_and_read_int32(void) {
    TEST("write_and_read_int32");
    const char *path = tmp_file("int32.meas");

    int32_t input[] = {-1000, 0, 42, 2147483647, (-2147483647 - 1)};
    int N = 5;

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    MeasGroupWriter *g = meas_writer_add_group(w, "G");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Count", MEAS_INT32);
    ASSERT_EQ_INT(meas_channel_write_i32(ch, input, N), 0);
    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasChannelData *rch =
        meas_group_channel_by_name(meas_reader_group(r, 0), "Count");
    ASSERT(rch != NULL);
    ASSERT_EQ_INT(rch->sample_count, N);

    int32_t out[5];
    ASSERT_EQ_INT(meas_channel_read_i32(rch, out, N), N);
    for (int i = 0; i < N; i++) ASSERT_EQ_INT(out[i], input[i]);

    meas_reader_close(r);
    PASS();
}

static void test_write_and_read_int64(void) {
    TEST("write_and_read_int64");
    const char *path = tmp_file("int64.meas");

    int64_t input[] = {INT64_MIN, -1LL, 0LL, 1LL, INT64_MAX};
    int N = 5;

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    MeasGroupWriter *g = meas_writer_add_group(w, "G");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Val", MEAS_INT64);
    ASSERT_EQ_INT(meas_channel_write_i64(ch, input, N), 0);
    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasChannelData *rch =
        meas_group_channel_by_name(meas_reader_group(r, 0), "Val");
    ASSERT(rch != NULL);
    ASSERT_EQ_INT(rch->sample_count, N);

    int64_t out[5];
    ASSERT_EQ_INT(meas_channel_read_i64(rch, out, N), N);
    for (int i = 0; i < N; i++) ASSERT_EQ_INT(out[i], input[i]);

    meas_reader_close(r);
    PASS();
}

static void test_write_and_read_timestamp(void) {
    TEST("write_and_read_timestamp");
    const char *path = tmp_file("timestamp.meas");

    /* nanoseconds since Unix epoch */
    int64_t input[] = {
        INT64_C(1741000000000000000),
        INT64_C(1741000001000000000),
        INT64_C(1741000002000000000),
    };
    int N = 3;

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    MeasGroupWriter *g = meas_writer_add_group(w, "G");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Time", MEAS_TIMESTAMP);
    ASSERT_EQ_INT(meas_channel_write_timestamp(ch, input, N), 0);
    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasChannelData *rch =
        meas_group_channel_by_name(meas_reader_group(r, 0), "Time");
    ASSERT(rch != NULL);
    ASSERT_EQ_INT(rch->data_type, MEAS_TIMESTAMP);
    ASSERT_EQ_INT(rch->sample_count, N);

    int64_t out[3];
    ASSERT_EQ_INT(meas_channel_read_timestamp(rch, out, N), N);
    for (int i = 0; i < N; i++) ASSERT_EQ_INT(out[i], input[i]);

    meas_reader_close(r);
    PASS();
}

static void test_multiple_groups_and_channels(void) {
    TEST("multiple_groups_and_channels");
    const char *path = tmp_file("multi.meas");

    float   rpm[]  = {1500.0f, 2000.0f, 2500.0f};
    double  temp[] = {85.0, 90.5, 95.2};
    int32_t gear[] = {1, 2, 3};

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);

    MeasGroupWriter *eng = meas_writer_add_group(w, "Engine");
    ASSERT(eng != NULL);
    MeasChannelWriter *rpm_ch  = meas_group_add_channel(eng, "RPM",         MEAS_FLOAT32);
    MeasChannelWriter *temp_ch = meas_group_add_channel(eng, "Temperature", MEAS_FLOAT64);
    ASSERT(meas_channel_write_f32(rpm_ch,  rpm,  3) == 0);
    ASSERT(meas_channel_write_f64(temp_ch, temp, 3) == 0);

    MeasGroupWriter *trans = meas_writer_add_group(w, "Transmission");
    ASSERT(trans != NULL);
    MeasChannelWriter *gear_ch = meas_group_add_channel(trans, "Gear", MEAS_INT32);
    ASSERT(meas_channel_write_i32(gear_ch, gear, 3) == 0);

    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    ASSERT_EQ_INT(meas_reader_group_count(r), 2);

    const MeasGroupData *g_eng = meas_reader_group_by_name(r, "Engine");
    ASSERT(g_eng != NULL);
    ASSERT_EQ_INT(g_eng->channel_count, 2);

    const MeasChannelData *r_rpm = meas_group_channel_by_name(g_eng, "RPM");
    ASSERT(r_rpm != NULL);
    ASSERT_EQ_INT(r_rpm->sample_count, 3);
    float rpm_out[3];
    ASSERT_EQ_INT(meas_channel_read_f32(r_rpm, rpm_out, 3), 3);
    for (int i = 0; i < 3; i++) ASSERT_NEAR(rpm_out[i], rpm[i], 1e-5f);

    const MeasGroupData *g_trans = meas_reader_group_by_name(r, "Transmission");
    ASSERT(g_trans != NULL);
    const MeasChannelData *r_gear = meas_group_channel_by_name(g_trans, "Gear");
    ASSERT(r_gear != NULL);
    int32_t gear_out[3];
    ASSERT_EQ_INT(meas_channel_read_i32(r_gear, gear_out, 3), 3);
    for (int i = 0; i < 3; i++) ASSERT_EQ_INT(gear_out[i], gear[i]);

    meas_reader_close(r);
    PASS();
}

static void test_incremental_flush(void) {
    TEST("incremental_flush");
    const char *path = tmp_file("streaming.meas");

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    MeasGroupWriter *g  = meas_writer_add_group(w, "Stream");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Signal", MEAS_FLOAT32);

    float batch1[] = {1.0f, 2.0f};
    float batch2[] = {3.0f, 4.0f};
    ASSERT_EQ_INT(meas_channel_write_f32(ch, batch1, 2), 0);
    ASSERT_EQ_INT(meas_writer_flush(w), 0);
    ASSERT_EQ_INT(meas_channel_write_f32(ch, batch2, 2), 0);
    meas_writer_close(w);  /* auto-flushes remaining */

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasChannelData *rch =
        meas_group_channel_by_name(meas_reader_group(r, 0), "Signal");
    ASSERT(rch != NULL);
    ASSERT_EQ_INT(rch->sample_count, 4);

    float out[4];
    ASSERT_EQ_INT(meas_channel_read_f32(rch, out, 4), 4);
    ASSERT_NEAR(out[0], 1.0f, 1e-6f);
    ASSERT_NEAR(out[1], 2.0f, 1e-6f);
    ASSERT_NEAR(out[2], 3.0f, 1e-6f);
    ASSERT_NEAR(out[3], 4.0f, 1e-6f);

    meas_reader_close(r);
    PASS();
}

static void test_channel_statistics(void) {
    TEST("channel_statistics");
    const char *path = tmp_file("stats.meas");

    double vals[] = {10.0, 20.0, 30.0, 40.0, 50.0};
    int N = 5;

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    MeasGroupWriter *g = meas_writer_add_group(w, "Stats");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Values", MEAS_FLOAT64);
    ASSERT_EQ_INT(meas_channel_write_f64(ch, vals, N), 0);
    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasChannelData *rch =
        meas_group_channel_by_name(meas_reader_group(r, 0), "Values");
    ASSERT(rch != NULL);
    ASSERT(rch->has_stats);
    ASSERT_EQ_INT(rch->stats.count, N);
    ASSERT_NEAR(rch->stats.min,      10.0,  1e-9);
    ASSERT_NEAR(rch->stats.max,      50.0,  1e-9);
    ASSERT_NEAR(rch->stats.mean,     30.0,  1e-9);
    ASSERT_NEAR(rch->stats.sum,      150.0, 1e-9);
    ASSERT_NEAR(rch->stats.first,    10.0,  1e-9);
    ASSERT_NEAR(rch->stats.last,     50.0,  1e-9);
    /* Population variance of [10,20,30,40,50] = 200 */
    ASSERT_NEAR(rch->stats.variance, 200.0, 1e-6);

    meas_reader_close(r);
    PASS();
}

static void test_binary_channel(void) {
    TEST("binary_channel");
    const char *path = tmp_file("binary.meas");

    /* Simulate three CAN frames */
    uint8_t frame0[] = {0x01, 0x02, 0x03};
    uint8_t frame1[] = {0xAA, 0xBB};
    uint8_t frame2[] = {0xFF};

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    MeasGroupWriter *g = meas_writer_add_group(w, "Bus");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Frames", MEAS_BINARY);
    ASSERT(meas_channel_write_frame(ch, frame0, 3) == 0);
    ASSERT(meas_channel_write_frame(ch, frame1, 2) == 0);
    ASSERT(meas_channel_write_frame(ch, frame2, 1) == 0);
    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasChannelData *rch =
        meas_group_channel_by_name(meas_reader_group(r, 0), "Frames");
    ASSERT(rch != NULL);
    ASSERT_EQ_INT(rch->data_type,    MEAS_BINARY);
    ASSERT_EQ_INT(rch->sample_count, 3);

    int64_t state = 0;
    const uint8_t *fd; int32_t fl;

    ASSERT_EQ_INT(meas_channel_next_frame(rch, &state, &fd, &fl), 1);
    ASSERT_EQ_INT(fl, 3);
    ASSERT_EQ_INT(memcmp(fd, frame0, 3), 0);

    ASSERT_EQ_INT(meas_channel_next_frame(rch, &state, &fd, &fl), 1);
    ASSERT_EQ_INT(fl, 2);
    ASSERT_EQ_INT(memcmp(fd, frame1, 2), 0);

    ASSERT_EQ_INT(meas_channel_next_frame(rch, &state, &fd, &fl), 1);
    ASSERT_EQ_INT(fl, 1);
    ASSERT_EQ_INT(fd[0], 0xFF);

    ASSERT_EQ_INT(meas_channel_next_frame(rch, &state, &fd, &fl), 0);  /* exhausted */

    meas_reader_close(r);
    PASS();
}

static void test_null_safety(void) {
    TEST("null_safety");
    /* All public functions should handle NULL without crashing */
    ASSERT(meas_writer_open(NULL) == NULL);
    ASSERT(meas_reader_open(NULL) == NULL);
    meas_writer_close(NULL);
    meas_reader_close(NULL);
    ASSERT(meas_reader_group_count(NULL) == 0);
    ASSERT(meas_reader_group(NULL, 0) == NULL);
    ASSERT(meas_reader_group_by_name(NULL, "x") == NULL);
    ASSERT(meas_group_channel_by_name(NULL, "x") == NULL);
    ASSERT(meas_writer_add_group(NULL, "x") == NULL);
    ASSERT(meas_group_add_channel(NULL, "x", MEAS_FLOAT64) == NULL);
    ASSERT(meas_channel_write_f64(NULL, NULL, 0) == -1);
    ASSERT(meas_channel_read_f64(NULL, NULL, 0) == -1);
    ASSERT(meas_writer_flush(NULL) == -1);
    PASS();
}

static void test_single_value_helpers(void) {
    TEST("single_value_helpers");
    const char *path = tmp_file("single.meas");

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    MeasGroupWriter *g = meas_writer_add_group(w, "G");
    MeasChannelWriter *f32ch = meas_group_add_channel(g, "F32",  MEAS_FLOAT32);
    MeasChannelWriter *f64ch = meas_group_add_channel(g, "F64",  MEAS_FLOAT64);
    MeasChannelWriter *i32ch = meas_group_add_channel(g, "I32",  MEAS_INT32);
    MeasChannelWriter *i64ch = meas_group_add_channel(g, "I64",  MEAS_INT64);
    MeasChannelWriter *boolch = meas_group_add_channel(g, "Bool", MEAS_BOOL);

    ASSERT_EQ_INT(meas_channel_write_f32_one(f32ch,  1.5f),  0);
    ASSERT_EQ_INT(meas_channel_write_f64_one(f64ch,  2.75),  0);
    ASSERT_EQ_INT(meas_channel_write_i32_one(i32ch,  -42),   0);
    ASSERT_EQ_INT(meas_channel_write_i64_one(i64ch,  12345), 0);
    ASSERT_EQ_INT(meas_channel_write_bool_one(boolch, 1),    0);
    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasGroupData *rg = meas_reader_group(r, 0);
    ASSERT(rg != NULL);

    float f32v; meas_channel_read_f32(meas_group_channel_by_name(rg, "F32"), &f32v, 1);
    ASSERT_NEAR(f32v, 1.5f, 1e-6f);

    double f64v; meas_channel_read_f64(meas_group_channel_by_name(rg, "F64"), &f64v, 1);
    ASSERT_NEAR(f64v, 2.75, 1e-12);

    int32_t i32v; meas_channel_read_i32(meas_group_channel_by_name(rg, "I32"), &i32v, 1);
    ASSERT_EQ_INT(i32v, -42);

    int64_t i64v; meas_channel_read_i64(meas_group_channel_by_name(rg, "I64"), &i64v, 1);
    ASSERT_EQ_INT(i64v, 12345);

    meas_reader_close(r);
    PASS();
}

static void test_string_channel(void) {
    TEST("string_channel");
    /* §7: Utf8String channels use the same [int32: len][bytes] frame format as Binary. */
    const char *path = tmp_file("strings.meas");

    const char *s0 = "Hello";
    const char *s1 = "MeasFlow";
    const char *s2 = "UTF-8: \xc3\xa9\xc3\xa0\xc3\xbc";  /* é à ü */

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    MeasGroupWriter *g = meas_writer_add_group(w, "Labels");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Name", MEAS_STRING);

    ASSERT_EQ_INT(meas_channel_write_string(ch, s0), 0);
    ASSERT_EQ_INT(meas_channel_write_string(ch, s1), 0);
    ASSERT_EQ_INT(meas_channel_write_string(ch, s2), 0);
    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasChannelData *rch =
        meas_group_channel_by_name(meas_reader_group(r, 0), "Name");
    ASSERT(rch != NULL);
    ASSERT_EQ_INT(rch->data_type,    MEAS_STRING);
    ASSERT_EQ_INT(rch->sample_count, 3);

    int64_t state = 0;
    const uint8_t *fd; int32_t fl;

    ASSERT_EQ_INT(meas_channel_next_frame(rch, &state, &fd, &fl), 1);
    ASSERT_EQ_INT(fl, (int32_t)strlen(s0));
    ASSERT_EQ_INT(memcmp(fd, s0, (size_t)fl), 0);

    ASSERT_EQ_INT(meas_channel_next_frame(rch, &state, &fd, &fl), 1);
    ASSERT_EQ_INT(fl, (int32_t)strlen(s1));
    ASSERT_EQ_INT(memcmp(fd, s1, (size_t)fl), 0);

    ASSERT_EQ_INT(meas_channel_next_frame(rch, &state, &fd, &fl), 1);
    ASSERT_EQ_INT(fl, (int32_t)strlen(s2));
    ASSERT_EQ_INT(memcmp(fd, s2, (size_t)fl), 0);

    ASSERT_EQ_INT(meas_channel_next_frame(rch, &state, &fd, &fl), 0);  /* exhausted */

    meas_reader_close(r);
    PASS();
}

static void test_can_frame_write_read(void) {
    TEST("can_frame_write_read");
    const char *path = tmp_file("can.meas");

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    MeasGroupWriter *g = meas_writer_add_group(w, "CAN1");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Frames", MEAS_BINARY);

    /* Standard CAN frame (11-bit id, 8 bytes) */
    MeasCanFrame f0 = {0};
    f0.arb_id = 0x1A2;
    f0.dlc    = 3;
    f0.flags  = 0; /* no BRS, no ESI, standard ID */
    f0.payload[0] = 0x11; f0.payload[1] = 0x22; f0.payload[2] = 0x33;
    ASSERT_EQ_INT(meas_channel_write_can_frame(ch, &f0), 0);

    /* CAN-FD frame (29-bit ext id, BRS, 12 bytes) */
    MeasCanFrame f1 = {0};
    f1.arb_id = 0x1FEDCBA;
    f1.dlc    = 4;
    f1.flags  = 0x05; /* BRS=1, ExtendedId=1 */
    f1.payload[0] = 0xAA; f1.payload[1] = 0xBB; f1.payload[2] = 0xCC; f1.payload[3] = 0xDD;
    ASSERT_EQ_INT(meas_channel_write_can_frame(ch, &f1), 0);

    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasChannelData *rch = meas_group_channel_by_name(meas_reader_group(r, 0), "Frames");
    ASSERT(rch != NULL);
    ASSERT_EQ_INT(rch->sample_count, 2);

    int64_t state = 0;
    MeasCanFrame out;

    ASSERT_EQ_INT(meas_channel_next_can_frame(rch, &state, &out), 1);
    ASSERT_EQ_INT((int)out.arb_id, 0x1A2);
    ASSERT_EQ_INT(out.dlc,  3);
    ASSERT_EQ_INT(out.flags, 0);
    ASSERT_EQ_INT(out.payload[0], 0x11);
    ASSERT_EQ_INT(out.payload[2], 0x33);

    ASSERT_EQ_INT(meas_channel_next_can_frame(rch, &state, &out), 1);
    ASSERT_EQ_INT((int)out.arb_id, 0x1FEDCBA);
    ASSERT_EQ_INT(out.dlc,  4);
    ASSERT_EQ_INT(out.flags, 0x05);
    ASSERT_EQ_INT(out.payload[0], 0xAA);
    ASSERT_EQ_INT(out.payload[3], 0xDD);

    ASSERT_EQ_INT(meas_channel_next_can_frame(rch, &state, &out), 0); /* exhausted */

    meas_reader_close(r);
    PASS();
}

static void test_lin_frame_write_read(void) {
    TEST("lin_frame_write_read");
    const char *path = tmp_file("lin.meas");

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    MeasGroupWriter *g = meas_writer_add_group(w, "LIN1");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Frames", MEAS_BINARY);

    MeasLinFrame f = {0};
    f.frame_id     = 0x12;
    f.dlc          = 4;
    f.nad          = 0x7F;
    f.checksum_type = 1; /* Enhanced */
    f.payload[0] = 0x01; f.payload[1] = 0x02; f.payload[2] = 0x03; f.payload[3] = 0x04;
    ASSERT_EQ_INT(meas_channel_write_lin_frame(ch, &f), 0);

    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasChannelData *rch = meas_group_channel_by_name(meas_reader_group(r, 0), "Frames");
    ASSERT(rch != NULL);
    ASSERT_EQ_INT(rch->sample_count, 1);

    int64_t state = 0;
    MeasLinFrame out;
    ASSERT_EQ_INT(meas_channel_next_lin_frame(rch, &state, &out), 1);
    ASSERT_EQ_INT(out.frame_id, 0x12);
    ASSERT_EQ_INT(out.dlc,      4);
    ASSERT_EQ_INT(out.nad,      0x7F);
    ASSERT_EQ_INT(out.checksum_type, 1);
    ASSERT_EQ_INT(out.payload[0], 0x01);
    ASSERT_EQ_INT(out.payload[3], 0x04);
    ASSERT_EQ_INT(meas_channel_next_lin_frame(rch, &state, &out), 0);

    meas_reader_close(r);
    PASS();
}

static void test_flexray_frame_write_read(void) {
    TEST("flexray_frame_write_read");
    const char *path = tmp_file("flexray.meas");

    uint8_t fr_payload[8] = {0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80};

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    MeasGroupWriter *g = meas_writer_add_group(w, "FR1");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Frames", MEAS_BINARY);

    MeasFlexRayFrame f = {0};
    f.slot_id         = 17;
    f.cycle_count     = 3;
    f.channel_flags   = 0x01; /* ChA */
    f.payload_length  = 8;
    f.payload         = fr_payload;
    ASSERT_EQ_INT(meas_channel_write_flexray_frame(ch, &f), 0);

    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasChannelData *rch = meas_group_channel_by_name(meas_reader_group(r, 0), "Frames");
    ASSERT(rch != NULL);
    ASSERT_EQ_INT(rch->sample_count, 1);

    int64_t state = 0;
    MeasFlexRayFrame out;
    ASSERT_EQ_INT(meas_channel_next_flexray_frame(rch, &state, &out), 1);
    ASSERT_EQ_INT(out.slot_id,        17);
    ASSERT_EQ_INT(out.cycle_count,    3);
    ASSERT_EQ_INT(out.channel_flags,  0x01);
    ASSERT_EQ_INT(out.payload_length, 8);
    ASSERT(out.payload != NULL);
    ASSERT_EQ_INT(out.payload[0], 0x10);
    ASSERT_EQ_INT(out.payload[7], 0x80);
    ASSERT_EQ_INT(meas_channel_next_flexray_frame(rch, &state, &out), 0);

    meas_reader_close(r);
    PASS();
}

static void test_ethernet_frame_write_read(void) {
    TEST("ethernet_frame_write_read");
    const char *path = tmp_file("eth.meas");

    uint8_t eth_payload[] = {0xDE, 0xAD, 0xBE, 0xEF};
    uint8_t mac_d[6] = {0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF};
    uint8_t mac_s[6] = {0x00, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E};

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    MeasGroupWriter *g = meas_writer_add_group(w, "ETH0");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Frames", MEAS_BINARY);

    MeasEthernetFrame f = {0};
    memcpy(f.mac_dest, mac_d, 6);
    memcpy(f.mac_src,  mac_s, 6);
    f.ether_type    = 0x0800; /* IPv4 */
    f.vlan_id       = 100;
    f.payload_length = 4;
    f.payload        = eth_payload;
    ASSERT_EQ_INT(meas_channel_write_ethernet_frame(ch, &f), 0);

    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasChannelData *rch = meas_group_channel_by_name(meas_reader_group(r, 0), "Frames");
    ASSERT(rch != NULL);
    ASSERT_EQ_INT(rch->sample_count, 1);

    int64_t state = 0;
    MeasEthernetFrame out;
    ASSERT_EQ_INT(meas_channel_next_ethernet_frame(rch, &state, &out), 1);
    ASSERT_EQ_INT(memcmp(out.mac_dest, mac_d, 6), 0);
    ASSERT_EQ_INT(memcmp(out.mac_src,  mac_s, 6), 0);
    ASSERT_EQ_INT(out.ether_type,     0x0800);
    ASSERT_EQ_INT(out.vlan_id,        100);
    ASSERT_EQ_INT(out.payload_length, 4);
    ASSERT(out.payload != NULL);
    ASSERT_EQ_INT(out.payload[0], 0xDE);
    ASSERT_EQ_INT(out.payload[3], 0xEF);
    ASSERT_EQ_INT(meas_channel_next_ethernet_frame(rch, &state, &out), 0);

    meas_reader_close(r);
    PASS();
}

static void test_bus_metadata_encode_decode(void) {
    TEST("bus_metadata_encode_decode");

    /* Build a CAN BusMetadata with one frame and one signal (no PDUs) */
    MeasValueDescription vdescs[2] = {
        { 0, "Off" },
        { 1, "On"  },
    };
    MeasSignalDefinition sig = {0};
    sig.name            = "EngineOn";
    sig.start_bit       = 0;
    sig.bit_length      = 1;
    sig.byte_order      = 0; /* Intel */
    sig.signal_type     = 0; /* Unsigned */
    sig.factor          = 1.0;
    sig.offset          = 0.0;
    sig.min_max_flags   = 0x03; /* hasMin, hasMax */
    sig.min_value       = 0.0;
    sig.max_value       = 1.0;
    sig.has_unit        = 0;
    sig.value_desc_count = 2;
    sig.value_descs     = vdescs;

    MeasFrameDefinition frame = {0};
    frame.name           = "EngineStatus";
    frame.frame_id       = 0x100;
    frame.payload_length = 8;
    frame.direction      = 0; /* Rx */
    frame.flags          = 0;
    frame.bus.can.is_extended_id = 0;
    frame.signal_count   = 1;
    frame.signals        = &sig;

    MeasValueTableEntry vte[2] = { {0, "Off"}, {1, "On"} };
    MeasValueTable vt = { "OnOff", 2, vte };

    MeasBusMetadata meta = {0};
    meta.format_version           = 1;
    meta.bus_config.bus_type      = MEAS_BUS_CAN;
    meta.bus_config.u.can.is_extended_id = 0;
    meta.bus_config.u.can.baud_rate      = 500000;
    meta.raw_frame_channel_name   = "Frames";
    meta.timestamp_channel_name   = "Timestamps";
    meta.frame_count              = 1;
    meta.frames                   = &frame;
    meta.value_table_count        = 1;
    meta.value_tables             = &vt;

    /* Encode */
    uint8_t *blob = NULL; int32_t blen = 0;
    ASSERT_EQ_INT(meas_bus_metadata_encode(&meta, &blob, &blen), 0);
    ASSERT(blob != NULL);
    ASSERT(blen > 0);

    /* Decode */
    MeasBusMetadata *decoded = NULL;
    ASSERT_EQ_INT(meas_bus_metadata_decode(blob, blen, &decoded), 0);
    ASSERT(decoded != NULL);

    /* Verify round-trip */
    ASSERT_EQ_INT(decoded->format_version, 1);
    ASSERT_EQ_INT(decoded->bus_config.bus_type,           MEAS_BUS_CAN);
    ASSERT_EQ_INT(decoded->bus_config.u.can.baud_rate,    500000);
    ASSERT(strcmp(decoded->raw_frame_channel_name, "Frames")     == 0);
    ASSERT(strcmp(decoded->timestamp_channel_name, "Timestamps") == 0);
    ASSERT_EQ_INT(decoded->frame_count, 1);
    ASSERT(decoded->frames != NULL);
    ASSERT(strcmp(decoded->frames[0].name, "EngineStatus") == 0);
    ASSERT_EQ_INT((int)decoded->frames[0].frame_id,    0x100);
    ASSERT_EQ_INT(decoded->frames[0].signal_count,    1);
    ASSERT(strcmp(decoded->frames[0].signals[0].name, "EngineOn") == 0);
    ASSERT_EQ_INT(decoded->frames[0].signals[0].start_bit,  0);
    ASSERT_EQ_INT(decoded->frames[0].signals[0].bit_length, 1);
    ASSERT_NEAR  (decoded->frames[0].signals[0].factor, 1.0, 1e-12);
    ASSERT_EQ_INT(decoded->frames[0].signals[0].value_desc_count, 2);
    ASSERT(strcmp(decoded->frames[0].signals[0].value_descs[0].description, "Off") == 0);
    ASSERT(strcmp(decoded->frames[0].signals[0].value_descs[1].description, "On")  == 0);
    ASSERT_EQ_INT(decoded->value_table_count, 1);
    ASSERT(strcmp(decoded->value_tables[0].name, "OnOff") == 0);
    ASSERT_EQ_INT(decoded->value_tables[0].entry_count, 2);

    free(blob);
    meas_bus_metadata_free(decoded);
    PASS();
}

static void test_bus_def_group_property(void) {
    TEST("bus_def_group_property");
    const char *path = tmp_file("busdef.meas");

    /* Build a minimal LIN BusMetadata */
    MeasBusMetadata meta = {0};
    meta.format_version              = 1;
    meta.bus_config.bus_type         = MEAS_BUS_LIN;
    meta.bus_config.u.lin.baud_rate  = 19200;
    meta.bus_config.u.lin.lin_version = 2;
    meta.raw_frame_channel_name      = "LinFrames";
    meta.timestamp_channel_name      = "LinTimes";

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    MeasGroupWriter *g = meas_writer_add_group(w, "LIN_BUS1");

    /* Attach bus metadata to the group */
    ASSERT_EQ_INT(meas_group_set_bus_def(g, &meta), 0);

    MeasChannelWriter *ch = meas_group_add_channel(g, "LinFrames", MEAS_BINARY);
    MeasLinFrame lf = {0};
    lf.frame_id = 0x3C; lf.dlc = 2;
    lf.payload[0] = 0xAB; lf.payload[1] = 0xCD;
    ASSERT_EQ_INT(meas_channel_write_lin_frame(ch, &lf), 0);

    meas_writer_close(w);

    /* Read back and verify bus_def property */
    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasGroupData *rg = meas_reader_group_by_name(r, "LIN_BUS1");
    ASSERT(rg != NULL);
    ASSERT(rg->property_count == 1);

    MeasBusMetadata *decoded = NULL;
    ASSERT_EQ_INT(meas_group_read_bus_def(rg, &decoded), 0);
    ASSERT(decoded != NULL);
    ASSERT_EQ_INT(decoded->bus_config.bus_type,           MEAS_BUS_LIN);
    ASSERT_EQ_INT(decoded->bus_config.u.lin.baud_rate,    19200);
    ASSERT_EQ_INT(decoded->bus_config.u.lin.lin_version,  2);
    ASSERT(strcmp(decoded->raw_frame_channel_name, "LinFrames") == 0);

    meas_bus_metadata_free(decoded);
    meas_reader_close(r);
    PASS();
}

/* ── Compression tests ──────────────────────────────────────────────────── */

static void test_compression_api(void) {
    TEST("compression_api");
    const char *path = tmp_file("comp_api.meas");

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);

    /* NONE is always supported */
    ASSERT_EQ_INT(meas_writer_set_compression(w, MEAS_COMPRESS_NONE), 0);

#ifdef MEAS_HAVE_LZ4
    ASSERT_EQ_INT(meas_writer_set_compression(w, MEAS_COMPRESS_LZ4), 0);
#else
    ASSERT_EQ_INT(meas_writer_set_compression(w, MEAS_COMPRESS_LZ4), -1);
#endif

#ifdef MEAS_HAVE_ZSTD
    ASSERT_EQ_INT(meas_writer_set_compression(w, MEAS_COMPRESS_ZSTD), 0);
#else
    ASSERT_EQ_INT(meas_writer_set_compression(w, MEAS_COMPRESS_ZSTD), -1);
#endif

    /* NULL writer */
    ASSERT_EQ_INT(meas_writer_set_compression(NULL, MEAS_COMPRESS_NONE), -1);

    meas_writer_close(w);
    PASS();
}

#ifdef MEAS_HAVE_LZ4
static void test_lz4_roundtrip(void) {
    TEST("lz4_roundtrip");
    const char *path = tmp_file("lz4.meas");

    double input[] = {1.0, 2.5, -3.14, 0.0, 1e6};
    int N = 5;

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    ASSERT_EQ_INT(meas_writer_set_compression(w, MEAS_COMPRESS_LZ4), 0);
    MeasGroupWriter *g = meas_writer_add_group(w, "Sensors");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Voltage", MEAS_FLOAT64);
    ASSERT_EQ_INT(meas_channel_write_f64(ch, input, N), 0);
    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    ASSERT_EQ_INT(meas_reader_group_count(r), 1);
    const MeasChannelData *rch =
        meas_group_channel_by_name(meas_reader_group(r, 0), "Voltage");
    ASSERT(rch != NULL);
    ASSERT_EQ_INT(rch->sample_count, N);

    double out[5];
    ASSERT_EQ_INT(meas_channel_read_f64(rch, out, N), N);
    for (int i = 0; i < N; i++) ASSERT_NEAR(out[i], input[i], 1e-12);

    meas_reader_close(r);
    PASS();
}

static void test_lz4_incremental_flush(void) {
    TEST("lz4_incremental_flush");
    const char *path = tmp_file("lz4_inc.meas");

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    ASSERT_EQ_INT(meas_writer_set_compression(w, MEAS_COMPRESS_LZ4), 0);
    MeasGroupWriter *g = meas_writer_add_group(w, "Stream");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Signal", MEAS_FLOAT32);

    float batch1[] = {1.0f, 2.0f, 3.0f};
    float batch2[] = {4.0f, 5.0f};
    ASSERT_EQ_INT(meas_channel_write_f32(ch, batch1, 3), 0);
    ASSERT_EQ_INT(meas_writer_flush(w), 0);
    ASSERT_EQ_INT(meas_channel_write_f32(ch, batch2, 2), 0);
    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasChannelData *rch =
        meas_group_channel_by_name(meas_reader_group(r, 0), "Signal");
    ASSERT(rch != NULL);
    ASSERT_EQ_INT(rch->sample_count, 5);

    float out[5];
    ASSERT_EQ_INT(meas_channel_read_f32(rch, out, 5), 5);
    for (int i = 0; i < 5; i++) ASSERT_NEAR(out[i], (float)(i + 1), 1e-6f);

    meas_reader_close(r);
    PASS();
}

static void test_lz4_statistics(void) {
    TEST("lz4_statistics");
    const char *path = tmp_file("lz4_stats.meas");

    double vals[] = {10.0, 20.0, 30.0, 40.0, 50.0};

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    ASSERT_EQ_INT(meas_writer_set_compression(w, MEAS_COMPRESS_LZ4), 0);
    MeasGroupWriter *g = meas_writer_add_group(w, "S");
    MeasChannelWriter *ch = meas_group_add_channel(g, "V", MEAS_FLOAT64);
    ASSERT_EQ_INT(meas_channel_write_f64(ch, vals, 5), 0);
    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasChannelData *rch =
        meas_group_channel_by_name(meas_reader_group(r, 0), "V");
    ASSERT(rch != NULL);
    ASSERT(rch->has_stats);
    ASSERT_EQ_INT(rch->stats.count, 5);
    ASSERT_NEAR(rch->stats.min,  10.0, 1e-9);
    ASSERT_NEAR(rch->stats.max,  50.0, 1e-9);
    ASSERT_NEAR(rch->stats.mean, 30.0, 1e-9);
    ASSERT_NEAR(rch->stats.variance, 200.0, 1e-6);

    meas_reader_close(r);
    PASS();
}

static void test_lz4_binary_frames(void) {
    TEST("lz4_binary_frames");
    const char *path = tmp_file("lz4_bin.meas");

    uint8_t f0[] = {0x01, 0x02, 0x03};
    uint8_t f1[] = {0xAA, 0xBB};

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    ASSERT_EQ_INT(meas_writer_set_compression(w, MEAS_COMPRESS_LZ4), 0);
    MeasGroupWriter *g = meas_writer_add_group(w, "Bus");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Frames", MEAS_BINARY);
    ASSERT_EQ_INT(meas_channel_write_frame(ch, f0, 3), 0);
    ASSERT_EQ_INT(meas_channel_write_frame(ch, f1, 2), 0);
    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasChannelData *rch =
        meas_group_channel_by_name(meas_reader_group(r, 0), "Frames");
    ASSERT(rch != NULL);
    ASSERT_EQ_INT(rch->sample_count, 2);

    int64_t state = 0;
    const uint8_t *fd; int32_t fl;
    ASSERT_EQ_INT(meas_channel_next_frame(rch, &state, &fd, &fl), 1);
    ASSERT_EQ_INT(fl, 3);
    ASSERT_EQ_INT(memcmp(fd, f0, 3), 0);
    ASSERT_EQ_INT(meas_channel_next_frame(rch, &state, &fd, &fl), 1);
    ASSERT_EQ_INT(fl, 2);
    ASSERT_EQ_INT(memcmp(fd, f1, 2), 0);
    ASSERT_EQ_INT(meas_channel_next_frame(rch, &state, &fd, &fl), 0);

    meas_reader_close(r);
    PASS();
}
#endif /* MEAS_HAVE_LZ4 */

#ifdef MEAS_HAVE_ZSTD
static void test_zstd_roundtrip(void) {
    TEST("zstd_roundtrip");
    const char *path = tmp_file("zstd.meas");

    double input[] = {1.0, 2.5, -3.14, 0.0, 1e6};
    int N = 5;

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    ASSERT_EQ_INT(meas_writer_set_compression(w, MEAS_COMPRESS_ZSTD), 0);
    MeasGroupWriter *g = meas_writer_add_group(w, "Sensors");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Voltage", MEAS_FLOAT64);
    ASSERT_EQ_INT(meas_channel_write_f64(ch, input, N), 0);
    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    ASSERT_EQ_INT(meas_reader_group_count(r), 1);
    const MeasChannelData *rch =
        meas_group_channel_by_name(meas_reader_group(r, 0), "Voltage");
    ASSERT(rch != NULL);
    ASSERT_EQ_INT(rch->sample_count, N);

    double out[5];
    ASSERT_EQ_INT(meas_channel_read_f64(rch, out, N), N);
    for (int i = 0; i < N; i++) ASSERT_NEAR(out[i], input[i], 1e-12);

    meas_reader_close(r);
    PASS();
}

static void test_zstd_incremental_flush(void) {
    TEST("zstd_incremental_flush");
    const char *path = tmp_file("zstd_inc.meas");

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    ASSERT_EQ_INT(meas_writer_set_compression(w, MEAS_COMPRESS_ZSTD), 0);
    MeasGroupWriter *g = meas_writer_add_group(w, "Stream");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Signal", MEAS_FLOAT32);

    float batch1[] = {1.0f, 2.0f, 3.0f};
    float batch2[] = {4.0f, 5.0f};
    ASSERT_EQ_INT(meas_channel_write_f32(ch, batch1, 3), 0);
    ASSERT_EQ_INT(meas_writer_flush(w), 0);
    ASSERT_EQ_INT(meas_channel_write_f32(ch, batch2, 2), 0);
    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasChannelData *rch =
        meas_group_channel_by_name(meas_reader_group(r, 0), "Signal");
    ASSERT(rch != NULL);
    ASSERT_EQ_INT(rch->sample_count, 5);

    float out[5];
    ASSERT_EQ_INT(meas_channel_read_f32(rch, out, 5), 5);
    for (int i = 0; i < 5; i++) ASSERT_NEAR(out[i], (float)(i + 1), 1e-6f);

    meas_reader_close(r);
    PASS();
}

static void test_zstd_statistics(void) {
    TEST("zstd_statistics");
    const char *path = tmp_file("zstd_stats.meas");

    double vals[] = {10.0, 20.0, 30.0, 40.0, 50.0};

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    ASSERT_EQ_INT(meas_writer_set_compression(w, MEAS_COMPRESS_ZSTD), 0);
    MeasGroupWriter *g = meas_writer_add_group(w, "S");
    MeasChannelWriter *ch = meas_group_add_channel(g, "V", MEAS_FLOAT64);
    ASSERT_EQ_INT(meas_channel_write_f64(ch, vals, 5), 0);
    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasChannelData *rch =
        meas_group_channel_by_name(meas_reader_group(r, 0), "V");
    ASSERT(rch != NULL);
    ASSERT(rch->has_stats);
    ASSERT_EQ_INT(rch->stats.count, 5);
    ASSERT_NEAR(rch->stats.min,  10.0, 1e-9);
    ASSERT_NEAR(rch->stats.max,  50.0, 1e-9);
    ASSERT_NEAR(rch->stats.mean, 30.0, 1e-9);
    ASSERT_NEAR(rch->stats.variance, 200.0, 1e-6);

    meas_reader_close(r);
    PASS();
}

static void test_zstd_binary_frames(void) {
    TEST("zstd_binary_frames");
    const char *path = tmp_file("zstd_bin.meas");

    uint8_t f0[] = {0x01, 0x02, 0x03};
    uint8_t f1[] = {0xAA, 0xBB};

    MeasWriter *w = meas_writer_open(path);
    ASSERT(w != NULL);
    ASSERT_EQ_INT(meas_writer_set_compression(w, MEAS_COMPRESS_ZSTD), 0);
    MeasGroupWriter *g = meas_writer_add_group(w, "Bus");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Frames", MEAS_BINARY);
    ASSERT_EQ_INT(meas_channel_write_frame(ch, f0, 3), 0);
    ASSERT_EQ_INT(meas_channel_write_frame(ch, f1, 2), 0);
    meas_writer_close(w);

    MeasReader *r = meas_reader_open(path);
    ASSERT(r != NULL);
    const MeasChannelData *rch =
        meas_group_channel_by_name(meas_reader_group(r, 0), "Frames");
    ASSERT(rch != NULL);
    ASSERT_EQ_INT(rch->sample_count, 2);

    int64_t state = 0;
    const uint8_t *fd; int32_t fl;
    ASSERT_EQ_INT(meas_channel_next_frame(rch, &state, &fd, &fl), 1);
    ASSERT_EQ_INT(fl, 3);
    ASSERT_EQ_INT(memcmp(fd, f0, 3), 0);
    ASSERT_EQ_INT(meas_channel_next_frame(rch, &state, &fd, &fl), 1);
    ASSERT_EQ_INT(fl, 2);
    ASSERT_EQ_INT(memcmp(fd, f1, 2), 0);
    ASSERT_EQ_INT(meas_channel_next_frame(rch, &state, &fd, &fl), 0);

    meas_reader_close(r);
    PASS();
}
#endif /* MEAS_HAVE_ZSTD */

static void test_cross_language_read(void) {
    TEST("cross_language_read_demo_file");
    /* Try to open the demo measurement file written by the C# implementation.
       This is an integration test that verifies cross-language compatibility.
       Skip gracefully if the file is not present. */
    const char *paths[] = {
        "demo_measurement.meas",           /* CWD = repo root (CI) */
        "../../demo_measurement.meas",     /* CWD = c/build (local) */
        "../demo_measurement.meas",        /* CWD = c/ */
    };
    MeasReader *r = NULL;
    for (int i = 0; i < 3; i++) {
        r = meas_reader_open(paths[i]);
        if (r) break;
    }
    if (!r) {
        printf("  [SKIP] demo_measurement.meas not found\n");
        g_passed++;  /* count as passed since it's an optional test */
        return;
    }
    ASSERT(meas_reader_group_count(r) > 0);
    meas_reader_close(r);
    PASS();
}

/* ── Main ───────────────────────────────────────────────────────────────── */

int main(void) {
    printf("MeasFlow C reader/writer tests\n");
    printf("==============================\n");

    test_write_and_read_float64();
    test_write_and_read_float32();
    test_write_and_read_int32();
    test_write_and_read_int64();
    test_write_and_read_timestamp();
    test_multiple_groups_and_channels();
    test_incremental_flush();
    test_channel_statistics();
    test_binary_channel();
    test_null_safety();
    test_single_value_helpers();
    test_string_channel();
    test_can_frame_write_read();
    test_lin_frame_write_read();
    test_flexray_frame_write_read();
    test_ethernet_frame_write_read();
    test_bus_metadata_encode_decode();
    test_bus_def_group_property();
    test_compression_api();
#ifdef MEAS_HAVE_LZ4
    test_lz4_roundtrip();
    test_lz4_incremental_flush();
    test_lz4_statistics();
    test_lz4_binary_frames();
#endif
#ifdef MEAS_HAVE_ZSTD
    test_zstd_roundtrip();
    test_zstd_incremental_flush();
    test_zstd_statistics();
    test_zstd_binary_frames();
#endif
    test_cross_language_read();

    printf("\n%d/%d tests passed", g_passed, g_tests);
    if (g_failed > 0) printf(", %d FAILED", g_failed);
    printf("\n");
    return g_failed > 0 ? 1 : 0;
}
