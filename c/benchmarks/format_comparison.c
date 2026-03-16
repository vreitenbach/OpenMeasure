/**
 * Format comparison benchmarks: MeasFlow vs HDF5 (libhdf5).
 *
 * Build (with vcpkg):
 *   vcpkg install hdf5
 *   cmake -B build -S c -DMEAS_BUILD_BENCHMARKS=ON \
 *     -DCMAKE_TOOLCHAIN_FILE=$VCPKG_INSTALLATION_ROOT/scripts/buildsystems/vcpkg.cmake
 *   cmake --build build --config Release
 *   ./build/bench_format_comparison    (or build/Release/bench_format_comparison.exe)
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include "../measflow.h"

#ifdef MEAS_HAVE_HDF5
#include <hdf5.h>
#endif

/* ── Timing helpers ─────────────────────────────────────────────────────── */

#ifdef _WIN32
#include <windows.h>
static double now_ms(void)
{
    LARGE_INTEGER freq, cnt;
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&cnt);
    return (double)cnt.QuadPart / (double)freq.QuadPart * 1000.0;
}
#else
static double now_ms(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec * 1000.0 + ts.tv_nsec / 1e6;
}
#endif

typedef struct {
    double median_ms;
    double min_ms;
    double max_ms;
} BenchResult;

static int cmp_double(const void *a, const void *b)
{
    double da = *(const double *)a, db = *(const double *)b;
    return (da > db) - (da < db);
}

static BenchResult bench(void (*fn)(const char *, const float *, int),
                          const char *path, const float *data, int n,
                          int warmup, int iterations)
{
    int i;
    for (i = 0; i < warmup; i++)
        fn(path, data, n);

    double *times = (double *)malloc((size_t)iterations * sizeof(double));
    for (i = 0; i < iterations; i++) {
        double t0 = now_ms();
        fn(path, data, n);
        times[i] = now_ms() - t0;
    }
    qsort(times, (size_t)iterations, sizeof(double), cmp_double);

    BenchResult r;
    r.median_ms = times[iterations / 2];
    r.min_ms = times[0];
    r.max_ms = times[iterations - 1];
    free(times);
    return r;
}

/* ── MeasFlow write/read ────────────────────────────────────────────────── */

static void write_measflow(const char *path, const float *data, int n)
{
    MeasWriter *w = meas_writer_open(path);
    MeasGroupWriter *g = meas_writer_add_group(w, "Data");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Signal", MEAS_FLOAT32);
    meas_channel_set_statistics(ch, 0);
    meas_channel_write_f32(ch, data, n);
    meas_writer_close(w);
}

static void read_measflow(const char *path, const float *data, int n)
{
    (void)data;
    MeasReader *r = meas_reader_open(path);
    if (!r) return;
    const MeasGroupData *grp = meas_reader_group_by_name(r, "Data");
    if (grp) {
        const MeasChannelData *ch = meas_group_channel_by_name(grp, "Signal");
        if (ch) {
            float *buf = (float *)malloc((size_t)n * sizeof(float));
            meas_channel_read_f32(ch, buf, n);
            free(buf);
        }
    }
    meas_reader_close(r);
}

static void write_measflow_10ch(const char *path, const float *data, int n)
{
    MeasWriter *w = meas_writer_open(path);
    MeasGroupWriter *g = meas_writer_add_group(w, "Data");
    char name[16];
    for (int c = 0; c < 10; c++) {
        snprintf(name, sizeof(name), "Ch%d", c);
        MeasChannelWriter *ch = meas_group_add_channel(g, name, MEAS_FLOAT32);
        meas_channel_set_statistics(ch, 0);
        meas_channel_write_f32(ch, data, n);
    }
    meas_writer_close(w);
}

static void read_measflow_10ch(const char *path, const float *data, int n)
{
    (void)data;
    MeasReader *r = meas_reader_open(path);
    if (!r) return;
    const MeasGroupData *grp = meas_reader_group_by_name(r, "Data");
    if (grp) {
        char name[16];
        float *buf = (float *)malloc((size_t)n * sizeof(float));
        for (int c = 0; c < 10; c++) {
            snprintf(name, sizeof(name), "Ch%d", c);
            const MeasChannelData *ch = meas_group_channel_by_name(grp, name);
            if (ch) meas_channel_read_f32(ch, buf, n);
        }
        free(buf);
    }
    meas_reader_close(r);
}

static void stream_measflow(const char *path, const float *data, int n)
{
    MeasWriter *w = meas_writer_open(path);
    MeasGroupWriter *g = meas_writer_add_group(w, "Data");
    MeasChannelWriter *ch = meas_group_add_channel(g, "Signal", MEAS_FLOAT32);
    meas_channel_set_statistics(ch, 0);
    int chunk = n / 10;
    int i;
    for (i = 0; i < 10; i++) {
        meas_channel_write_f32(ch, data + i * chunk, chunk);
        meas_writer_flush(w);
    }
    meas_writer_close(w);
}

/* ── HDF5 write/read ────────────────────────────────────────────────────── */

#ifdef MEAS_HAVE_HDF5
static void write_hdf5(const char *path, const float *data, int n)
{
    hid_t file = H5Fcreate(path, H5F_ACC_TRUNC, H5P_DEFAULT, H5P_DEFAULT);
    hid_t grp = H5Gcreate2(file, "Data", H5P_DEFAULT, H5P_DEFAULT, H5P_DEFAULT);
    hsize_t dims[1] = { (hsize_t)n };
    hid_t space = H5Screate_simple(1, dims, NULL);
    hid_t dset = H5Dcreate2(grp, "Signal", H5T_IEEE_F32LE, space,
                              H5P_DEFAULT, H5P_DEFAULT, H5P_DEFAULT);
    H5Dwrite(dset, H5T_NATIVE_FLOAT, H5S_ALL, H5S_ALL, H5P_DEFAULT, data);
    H5Dclose(dset);
    H5Sclose(space);
    H5Gclose(grp);
    H5Fclose(file);
}

static void read_hdf5(const char *path, const float *data, int n)
{
    (void)data;
    hid_t file = H5Fopen(path, H5F_ACC_RDONLY, H5P_DEFAULT);
    hid_t dset = H5Dopen2(file, "/Data/Signal", H5P_DEFAULT);
    float *buf = (float *)malloc((size_t)n * sizeof(float));
    H5Dread(dset, H5T_NATIVE_FLOAT, H5S_ALL, H5S_ALL, H5P_DEFAULT, buf);
    free(buf);
    H5Dclose(dset);
    H5Fclose(file);
}

static void read_hdf5_10ch(const char *path, const float *data, int n)
{
    (void)data;
    hid_t file = H5Fopen(path, H5F_ACC_RDONLY, H5P_DEFAULT);
    float *buf = (float *)malloc((size_t)n * sizeof(float));
    char name[32];
    for (int c = 0; c < 10; c++) {
        snprintf(name, sizeof(name), "/Data/Ch%d", c);
        hid_t dset = H5Dopen2(file, name, H5P_DEFAULT);
        H5Dread(dset, H5T_NATIVE_FLOAT, H5S_ALL, H5S_ALL, H5P_DEFAULT, buf);
        H5Dclose(dset);
    }
    free(buf);
    H5Fclose(file);
}

static void write_hdf5_10ch(const char *path, const float *data, int n)
{
    hid_t file = H5Fcreate(path, H5F_ACC_TRUNC, H5P_DEFAULT, H5P_DEFAULT);
    hid_t grp = H5Gcreate2(file, "Data", H5P_DEFAULT, H5P_DEFAULT, H5P_DEFAULT);
    hsize_t dims[1] = { (hsize_t)n };
    hid_t space = H5Screate_simple(1, dims, NULL);
    char name[16];
    for (int c = 0; c < 10; c++) {
        snprintf(name, sizeof(name), "Ch%d", c);
        hid_t dset = H5Dcreate2(grp, name, H5T_IEEE_F32LE, space,
                                  H5P_DEFAULT, H5P_DEFAULT, H5P_DEFAULT);
        H5Dwrite(dset, H5T_NATIVE_FLOAT, H5S_ALL, H5S_ALL, H5P_DEFAULT, data);
        H5Dclose(dset);
    }
    H5Sclose(space);
    H5Gclose(grp);
    H5Fclose(file);
}

static void stream_hdf5(const char *path, const float *data, int n)
{
    /* HDF5 does not support incremental writes without extensible datasets.
       Write all data at once for a fair "best case" comparison. */
    write_hdf5(path, data, n);
}
#endif

/* ── File size helper ───────────────────────────────────────────────────── */

static long file_size(const char *path)
{
    FILE *f = fopen(path, "rb");
    if (!f) return 0;
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fclose(f);
    return sz;
}

/* ── Print helpers ──────────────────────────────────────────────────────── */

static void print_header(const char *title)
{
    printf("\n");
    for (int i = 0; i < 60; i++) putchar('-');
    printf("\n  %s\n", title);
    for (int i = 0; i < 60; i++) putchar('-');
    printf("\n");
}

static void print_result(const char *label, BenchResult r)
{
    printf("  %s: %8.2f ms\n", label, r.median_ms);
}

static void print_size(const char *label, long bytes)
{
    printf("  %s: %8.1f KB\n", label, bytes / 1024.0);
}

/* ── Main ───────────────────────────────────────────────────────────────── */

int main(void)
{
    int sample_counts[] = { 100000, 1000000 };
    int num_counts = 2;
    int s;

    for (s = 0; s < num_counts; s++) {
        int n = sample_counts[s];

        /* Generate test data */
        float *data = (float *)malloc((size_t)n * sizeof(float));
        srand(42);
        for (int i = 0; i < n; i++)
            data[i] = (float)rand() / (float)RAND_MAX * 10000.0f;

        char meas_path[256], h5_path[256], raw_path[256];
        snprintf(meas_path, sizeof(meas_path), "bench_%d.meas", n);
        snprintf(h5_path, sizeof(h5_path), "bench_%d.h5", n);
        snprintf(raw_path, sizeof(raw_path), "bench_%d.bin", n);

        printf("\n============================================================\n");
        printf("  Format comparison (C) -- %d samples\n", n);
        printf("============================================================\n");

        /* Write benchmarks */
        print_header("Write 1 channel");
        print_result("MeasFlow", bench(write_measflow, meas_path, data, n, 1, 5));
#ifdef MEAS_HAVE_HDF5
        print_result("HDF5 (libhdf5)", bench(write_hdf5, h5_path, data, n, 1, 5));
#endif

        /* Write 10 channels */
        print_header("Write 10 channels");
        {
            char meas10_path[256], h510_path[256];
            snprintf(meas10_path, sizeof(meas10_path), "bench_%d_10ch.meas", n);
            snprintf(h510_path, sizeof(h510_path), "bench_%d_10ch.h5", n);
            print_result("MeasFlow", bench(write_measflow_10ch, meas10_path, data, n, 1, 5));
#ifdef MEAS_HAVE_HDF5
            print_result("HDF5 (libhdf5)", bench(write_hdf5_10ch, h510_path, data, n, 1, 5));
#endif
            remove(meas10_path);
            remove(h510_path);
        }

        /* Read benchmarks */
        write_measflow(meas_path, data, n);
#ifdef MEAS_HAVE_HDF5
        write_hdf5(h5_path, data, n);
#endif

        print_header("Read 1 channel");
        print_result("MeasFlow", bench(read_measflow, meas_path, data, n, 1, 5));
#ifdef MEAS_HAVE_HDF5
        print_result("HDF5 (libhdf5)", bench(read_hdf5, h5_path, data, n, 1, 5));
#endif

        /* Read 10 channels */
        print_header("Read 10 channels");
        {
            char meas10_path[256], h510_path[256];
            snprintf(meas10_path, sizeof(meas10_path), "bench_%d_10ch.meas", n);
            snprintf(h510_path, sizeof(h510_path), "bench_%d_10ch.h5", n);
            write_measflow_10ch(meas10_path, data, n);
            print_result("MeasFlow", bench(read_measflow_10ch, meas10_path, data, n, 1, 5));
#ifdef MEAS_HAVE_HDF5
            write_hdf5_10ch(h510_path, data, n);
            print_result("HDF5 (libhdf5)", bench(read_hdf5_10ch, h510_path, data, n, 1, 5));
#endif
            remove(meas10_path);
            remove(h510_path);
        }

        /* Streaming write (MeasFlow only — HDF5 has no streaming support) */
        print_header("Streaming write");
        print_result("MeasFlow (10 flushes)", bench(stream_measflow, meas_path, data, n, 1, 5));

        /* File size */
        write_measflow(meas_path, data, n);
        print_header("File size");
        print_size("MeasFlow", file_size(meas_path));
#ifdef MEAS_HAVE_HDF5
        write_hdf5(h5_path, data, n);
        print_size("HDF5 (libhdf5)", file_size(h5_path));
#endif
        /* Raw binary baseline */
        {
            FILE *f = fopen(raw_path, "wb");
            fwrite(data, sizeof(float), (size_t)n, f);
            fclose(f);
            print_size("Raw binary", file_size(raw_path));
        }

        /* Cleanup */
        remove(meas_path);
        remove(h5_path);
        remove(raw_path);
        free(data);
    }

#ifndef MEAS_HAVE_HDF5
    printf("\n  Note: HDF5 not available. Install via: vcpkg install hdf5\n");
    printf("  Build with -DMEAS_BUILD_BENCHMARKS=ON to include HDF5 comparison.\n");
#endif

    return 0;
}
