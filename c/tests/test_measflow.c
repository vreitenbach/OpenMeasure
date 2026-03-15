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

    int32_t input[] = {-1000, 0, 42, 2147483647, -2147483648};
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

static void test_cross_language_read(void) {
    TEST("cross_language_read_demo_file");
    /* Try to open the demo measurement file written by the C# implementation.
       This is an integration test that verifies cross-language compatibility.
       Skip gracefully if the file is not present. */
    const char *demo = "../../demo_measurement.meas";
    MeasReader *r = meas_reader_open(demo);
    if (!r) {
        printf("  [SKIP] demo_measurement.meas not found at %s\n", demo);
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
    test_cross_language_read();

    printf("\n%d/%d tests passed", g_passed, g_tests);
    if (g_failed > 0) printf(", %d FAILED", g_failed);
    printf("\n");
    return g_failed > 0 ? 1 : 0;
}
