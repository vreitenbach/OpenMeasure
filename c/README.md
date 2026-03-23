# MeasFlow C Reader/Writer

A C99 implementation of the MeasFlow (`.meas`) binary format reader and writer,
conforming to the [format specification](../SPECIFICATION.md) v1.

## Features

- **Full roundtrip** — write and read back all fixed-size numeric types
  (`int8` through `uint64`, `float32`, `float64`, `bool`, `timestamp`, `timespan`)
- **Variable-length binary frames** — for raw bus data (CAN frames, LIN messages, etc.)
- **Streaming writes** — call `meas_writer_flush()` multiple times to produce
  multi-segment files readable by all other implementations
- **Channel statistics** — Welford's online algorithm; stats are computed during
  writing and embedded in the metadata segment for zero-cost reads
- **Cross-language compatible** — files written by C can be read by Python/C#,
  and vice versa
- **Big-endian safe** — explicit little-endian byte-order conversion for all
  multi-byte values
- **Minimal dependencies** — C99 standard library + optional LZ4/Zstd compression

## Building

Requires CMake >= 3.14 and a C99-capable compiler.

LZ4 and Zstd compression are enabled by default. To build with compression support,
provide the dependencies via vcpkg or your system package manager:

```sh
cd c/

# With compression (default — requires lz4 + zstd):
cmake -B build -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_TOOLCHAIN_FILE=$VCPKG_INSTALLATION_ROOT/scripts/buildsystems/vcpkg.cmake
cmake --build build
ctest --test-dir build

# Without compression:
cmake -B build -DCMAKE_BUILD_TYPE=Release \
  -DMEAS_WITH_LZ4=OFF -DMEAS_WITH_ZSTD=OFF
cmake --build build
ctest --test-dir build
```

| CMake Option | Default | Description |
|--------------|---------|-------------|
| `MEAS_WITH_LZ4` | `ON` | Enable LZ4 compression (requires `lz4` package) |
| `MEAS_WITH_ZSTD` | `ON` | Enable Zstd compression (requires `zstd` package) |
| `MEAS_BUILD_TESTS` | `ON` | Build unit tests |
| `MEAS_BUILD_QUICKSTART` | `OFF` | Build quickstart example |
| `MEAS_BUILD_BENCHMARKS` | `OFF` | Build benchmarks (optional HDF5) |

To compile without CMake (no compression):

```sh
cc -std=c99 -I. -o my_app my_app.c measflow.c
```

## Quick Start

### Writing

```c
#include "measflow.h"

MeasWriter *w = meas_writer_open("data.meas");

MeasGroupWriter  *g  = meas_writer_add_group(w, "Motor");
MeasChannelWriter *rpm  = meas_group_add_channel(g, "RPM",         MEAS_FLOAT32);
MeasChannelWriter *temp = meas_group_add_channel(g, "Temperature", MEAS_FLOAT64);

float  rpm_data[]  = {1500.0f, 2000.0f, 2500.0f};
double temp_data[] = {85.0, 90.5, 95.2};

meas_channel_write_f32(rpm,  rpm_data,  3);
meas_channel_write_f64(temp, temp_data, 3);

meas_writer_close(w);   /* flushes all data and finalises the header */
```

Multiple flushes (streaming / incremental):

```c
meas_channel_write_f32(rpm, first_batch, 100);
meas_writer_flush(w);       /* write Data segment 1 */

meas_channel_write_f32(rpm, second_batch, 100);
meas_writer_close(w);       /* auto-flush + finalise */
```

Binary frames (CAN / LIN / raw bus data):

```c
MeasChannelWriter *frames = meas_group_add_channel(g, "Frames", MEAS_BINARY);
uint8_t can_frame[] = {0x01, 0x23, 0xDE, 0xAD, 0xBE, 0xEF};
meas_channel_write_frame(frames, can_frame, sizeof(can_frame));
```

### Reading

```c
#include "measflow.h"

MeasReader *r = meas_reader_open("data.meas");

const MeasGroupData   *g   = meas_reader_group_by_name(r, "Motor");
const MeasChannelData *rpm = meas_group_channel_by_name(g, "RPM");

printf("Samples: %lld\n", (long long)rpm->sample_count);

float *buf = malloc(rpm->sample_count * sizeof(float));
meas_channel_read_f32(rpm, buf, rpm->sample_count);

/* Pre-computed statistics (no need to read all data) */
if (rpm->has_stats) {
    printf("Min: %.2f  Max: %.2f  Mean: %.2f\n",
           rpm->stats.min, rpm->stats.max, rpm->stats.mean);
}

free(buf);
meas_reader_close(r);
```

Iterating over binary/variable-length frames:

```c
const MeasChannelData *frames = meas_group_channel_by_name(g, "Frames");
int64_t state = 0;
const uint8_t *data; int32_t len;
while (meas_channel_next_frame(frames, &state, &data, &len) == 1) {
    /* process data[0..len-1] */
}
```

## API Reference

See [`measflow.h`](measflow.h) for full documentation of all types and functions.

### Key types

| Type | Description |
|------|-------------|
| `MeasWriter`        | Open writer handle |
| `MeasGroupWriter`   | Group within a writer |
| `MeasChannelWriter` | Channel within a group (writer side) |
| `MeasReader`        | Open reader handle |
| `MeasGroupData`     | Decoded group with channels and properties |
| `MeasChannelData`   | Decoded channel with typed data and optional stats |
| `MeasProperty`      | Typed key-value property |
| `MeasChannelStats`  | Pre-computed channel statistics |

### Data type constants

| Constant | C type | Bytes |
|----------|--------|-------|
| `MEAS_INT8` … `MEAS_INT64` | `int8_t` … `int64_t` | 1–8 |
| `MEAS_UINT8` … `MEAS_UINT64` | `uint8_t` … `uint64_t` | 1–8 |
| `MEAS_FLOAT32` | `float` | 4 |
| `MEAS_FLOAT64` | `double` | 8 |
| `MEAS_TIMESTAMP` | `int64_t` (nanoseconds) | 8 |
| `MEAS_TIMESPAN` | `int64_t` (nanoseconds) | 8 |
| `MEAS_BINARY` | variable-length frames | — |
| `MEAS_BOOL` | `uint8_t` (0/1) | 1 |

## Cross-Language Compatibility

Files written by the C implementation are readable by the Python and C# implementations,
and vice versa. This is verified by the `cross_language_read_demo_file` test case
which opens the `demo_measurement.meas` file produced by the C# writer.
