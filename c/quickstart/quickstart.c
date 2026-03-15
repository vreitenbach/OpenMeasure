/*
 * quickstart.c — MeasFlow C reader/writer quickstart
 *
 * Compile (from the c/ directory):
 *   cc -std=c99 -I. -o quickstart/quickstart quickstart/quickstart.c measflow.c -lm
 *
 * Or with CMake (MEAS_BUILD_QUICKSTART=ON):
 *   cmake -B build -DMEAS_BUILD_QUICKSTART=ON
 *   cmake --build build
 *   ./build/quickstart
 */

#include "measflow.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>

#define OUTPUT "quickstart.meas"
#define N      100

int main(void)
{
    int i;
    float  rpm_data[N];
    double temp_data[N];

    for (i = 0; i < N; i++) {
        rpm_data[i]  = 3000.0f + (float)(sin(i * 0.1) * 500.0);
        temp_data[i] = 90.0    + sin(i * 0.05) * 5.0;
    }

    /* ── Write ────────────────────────────────────────────────────────── */
    printf("=== Writing measurement data ===\n");

    MeasWriter *w = meas_writer_open(OUTPUT);
    if (!w) { fprintf(stderr, "Failed to open writer\n"); return 1; }

    MeasGroupWriter  *motor = meas_writer_add_group(w, "Motor");
    MeasChannelWriter *rpm  = meas_group_add_channel(motor, "RPM",            MEAS_FLOAT32);
    MeasChannelWriter *temp = meas_group_add_channel(motor, "OilTemperature", MEAS_FLOAT64);

    meas_channel_write_f32(rpm,  rpm_data,  N);
    meas_channel_write_f64(temp, temp_data, N);

    meas_writer_close(w);
    printf("  Written: %s  (%d samples per channel)\n", OUTPUT, N);

    /* ── Read ─────────────────────────────────────────────────────────── */
    printf("\n=== Reading measurement data ===\n");

    MeasReader *r = meas_reader_open(OUTPUT);
    if (!r) { fprintf(stderr, "Failed to open reader\n"); return 1; }

    printf("  Groups: %d\n", meas_reader_group_count(r));

    const MeasGroupData   *motor_r = meas_reader_group_by_name(r, "Motor");
    const MeasChannelData *rpm_r   = meas_group_channel_by_name(motor_r, "RPM");
    const MeasChannelData *temp_r  = meas_group_channel_by_name(motor_r, "OilTemperature");

    float  *rpm_buf  = (float  *)malloc((size_t)rpm_r->sample_count  * sizeof(float));
    double *temp_buf = (double *)malloc((size_t)temp_r->sample_count * sizeof(double));

    meas_channel_read_f32(rpm_r,  rpm_buf,  rpm_r->sample_count);
    meas_channel_read_f64(temp_r, temp_buf, temp_r->sample_count);

    printf("  RPM            : %lld samples, first=%.1f, last=%.1f\n",
           (long long)rpm_r->sample_count,
           rpm_buf[0], rpm_buf[rpm_r->sample_count - 1]);
    printf("  OilTemperature : %lld samples, first=%.1f\n",
           (long long)temp_r->sample_count, temp_buf[0]);

    /* Pre-computed statistics — no re-reading needed */
    if (rpm_r->has_stats) {
        printf("\n  RPM statistics (pre-computed, zero re-read cost):\n");
        printf("    count=%lld  min=%.1f  max=%.1f  mean=%.1f\n",
               (long long)rpm_r->sample_count,
               rpm_r->stats.min, rpm_r->stats.max, rpm_r->stats.mean);
    }

    free(rpm_buf);
    free(temp_buf);
    meas_reader_close(r);

    printf("\nDone.\n");
    return 0;
}
