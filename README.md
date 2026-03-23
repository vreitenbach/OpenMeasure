# MeasFlow (.meas)

> **⚠️ Prototype:** This project is in prototype stage. The API, file format, and feature set may still change significantly. Not intended for production use.

Open, high-performance measurement data format with multi-language support. Simple like TDMS, powerful like MDF4.

**MIT License** | **Minimal dependencies** (optional LZ4/Zstd compression)

[![NuGet](https://img.shields.io/nuget/v/MeasFlow)](https://www.nuget.org/packages/MeasFlow)
[![PyPI](https://img.shields.io/pypi/v/measflow)](https://pypi.org/project/measflow/)
[![vcpkg](https://img.shields.io/badge/vcpkg-registry-blue)](https://github.com/vreitenbach/vcpkg-registry)
[![CI](https://github.com/vreitenbach/MeasFlow/actions/workflows/ci.yml/badge.svg)](https://github.com/vreitenbach/MeasFlow/actions/workflows/ci.yml)

## Why?

Existing formats have limitations:
- **TDMS** (NI) — proprietary, limited bus data support
- **HDF5** — complex C-based API, poor .NET interop
- **MDF4** (ASAM) — extremely complex spec, restricted tooling

MeasFlow provides a clean, open alternative with first-class support for automotive bus data (CAN, CAN-FD, LIN, FlexRay, Ethernet) and AUTOSAR concepts (PDU, Container-PDU, Multiplexing, E2E, SecOC).

## Installation

```sh
# C# / .NET
dotnet add package MeasFlow

# Python
pip install measflow

# C (vcpkg — custom registry until official vcpkg publication)
vcpkg install measflow
```

## Language Bindings

| Language | Directory | Install | Quick Start |
|----------|-----------|---------|-------------|
| **C#** (.NET 10) | [`csharp/`](csharp/) | [`NuGet`](https://www.nuget.org/packages/MeasFlow) | [`csharp/samples/QuickStart/`](csharp/samples/QuickStart/) |
| **Python** (≥ 3.10) | [`python/`](python/) | [`PyPI`](https://pypi.org/project/measflow/) | [`python/quickstart/quickstart.py`](python/quickstart/quickstart.py) |
| **C** (C99) | [`c/`](c/) | [vcpkg](https://github.com/vreitenbach/vcpkg-registry) | [`c/quickstart/quickstart.c`](c/quickstart/quickstart.c) |

Each binding is self-contained with its own README, tests, and a runnable quickstart.

### C# Quick Start

```sh
cd csharp/samples/QuickStart
dotnet run
```

### Python Quick Start

```sh
cd python
pip install -e .
python quickstart/quickstart.py
```

### C Quick Start

To use measflow via vcpkg, add a `vcpkg-configuration.json` to your project (required until the port is published to the official vcpkg registry):

```json
{
  "default-registry": null,
  "registries": [
    {
      "kind": "git",
      "repository": "https://github.com/vreitenbach/vcpkg-registry",
      "baseline": "d6473e3037973c8a5d465ef3fc1955b8e4f58557",
      "packages": ["measflow"]
    }
  ]
}
```

```sh
cd c

# With compression (requires lz4 + zstd via vcpkg):
cmake -B build -DMEAS_BUILD_QUICKSTART=ON \
  -DCMAKE_TOOLCHAIN_FILE=$VCPKG_INSTALLATION_ROOT/scripts/buildsystems/vcpkg.cmake
cmake --build build

# Without compression:
cmake -B build -DMEAS_BUILD_QUICKSTART=ON \
  -DMEAS_WITH_LZ4=OFF -DMEAS_WITH_ZSTD=OFF
cmake --build build

./build/quickstart
```

## Features

### Core
- Streaming write with incremental flush
- Typed channels: `int8..uint64`, `float32/64`, `bool`, `timestamp`
- Properties at file, group, and channel level
- Nanosecond-precision timestamps (`MeasTimestamp`)
- Inline channel statistics (Min/Max/Mean/StdDev) — computed during write, available instantly on read

### Bus Data (MDF4-compatible)
- **Bus types**: CAN 2.0A/B, CAN-FD, LIN, FlexRay, Ethernet, MOST
- **Polymorphic frame definitions** with bus-specific metadata
- **Signal definitions**: start bit, bit length, Intel/Motorola byte order, factor/offset, value descriptions
- **PDU layer**: AUTOSAR I-PDU with Container-PDU and Contained-PDU support
- **Multiplexing**: MUX signals with nested multiplexing and range conditions
- **E2E Protection**: AUTOSAR Profile 01-11, CRC/Counter positions
- **SecOC**: Secure Onboard Communication with CMAC-AES/HMAC-SHA, freshness values, key management
- **Signal decoding**: `group.DecodeSignal("RPM")` extracts values directly from raw frames

### Architecture
```
BusChannelDefinition
 ├── BusConfig (CAN/CAN-FD/LIN/FlexRay/Ethernet/MOST)
 ├── Frames[]
 │    ├── FrameId, PayloadLength, Flags, Direction
 │    ├── Signals[] (direct signals when no PDU layer)
 │    └── Pdus[]
 │         ├── Signals[]
 │         ├── MultiplexConfig
 │         ├── E2EProtection
 │         ├── SecOcConfig
 │         └── ContainedPdus[] (AUTOSAR I-PDU Mux)
 └── ValueTables[]
```

## Streaming Architecture

MeasFlow is **streaming-first** — the format is designed so data can be written and read incrementally without holding the entire file in memory.

### Streaming Writes

```csharp
using var writer = MeasFile.CreateWriter("live_recording.meas");
var group = writer.AddGroup("Sensors");
var temp = group.AddChannel<float>("Temperature");

// Write and flush in chunks — memory stays bounded
while (recording)
{
    float[] batch = ReadFromSensor(batchSize: 10_000);
    temp.Write(batch.AsSpan());
    writer.Flush();  // → new data segment on disk, buffer cleared
}
// Dispose writes remaining data and patches header
```

Each `Flush()` creates a new Data segment on disk. The writer only keeps one chunk in memory at a time, regardless of total recording duration.

### Streaming Reads

```csharp
using var reader = MeasFile.OpenRead("live_recording.meas");
var channel = reader["Sensors"]["Temperature"];

// Chunk-by-chunk: one segment at a time in memory
foreach (var chunk in channel.ReadChunks<float>())
{
    Process(chunk.Span);  // Only one chunk loaded at a time
}

// Or: instant statistics without reading any data
var stats = channel.Statistics;
Console.WriteLine($"Mean: {stats?.Mean}, StdDev: {stats?.StdDev}");
```

### Segment-Linked File Layout

```
┌───────────────────┐
│  File Header 64B  │  Magic: MEAS\0, Version, GUID, Timestamp
├───────────────────┤
│  Metadata Segment │  Groups, channels, properties, bus definitions
│  → NextOffset ────┼──┐
├───────────────────┤  │
│  Data Segment #1  │◄─┘  First Flush() result
│  → NextOffset ────┼──┐
├───────────────────┤  │
│  Data Segment #2  │◄─┘  Second Flush() result
│  → NextOffset ────┼──┐
├───────────────────┤  │
│  Data Segment #N  │◄─┘  Final Dispose() data
└───────────────────┘
```

Segments form a forward-linked list — readers scan sequentially, no random access needed. Partial files (writer still open) can be read up to the last complete segment.

> **Full specification**: See [SPECIFICATION.md](SPECIFICATION.md) for the complete binary format, including byte offsets, data type encoding, bus metadata, and conformance requirements.

## File Format Summary

| Layer | Content |
|-------|---------|
| **Header** (64 B) | Magic `MEAS\0`, version, segment count, GUID, creation timestamp |
| **Metadata Segment** | Group/channel definitions, bus definitions, properties, statistics |
| **Data Segments** (repeated) | Chunked channel data: fixed-size arrays or length-prefixed raw frames |

Raw frame wire format per bus type:

| Bus | Layout |
|-----|--------|
| CAN/CAN-FD | `[uint32 arbId] [byte dlc] [byte flags] [payload]` |
| LIN | `[byte frameId] [byte dlc] [byte nad] [byte checksum] [payload]` |
| FlexRay | `[uint16 slotId] [byte cycle] [byte flags] [uint16 len] [payload]` |
| Ethernet | `[6B macDst] [6B macSrc] [uint16 etherType] [uint16 vlan] [uint16 len] [payload]` |

## Benchmarks

All three language bindings include format comparison benchmarks (MeasFlow vs HDF5) and cross-language performance tests.

### C# (BenchmarkDotNet)

```bash
cd csharp/benchmarks/MeasFlow.Benchmarks
dotnet run -c Release -- --filter "*FormatComparison*"   # MeasFlow vs HDF5 (PureHDF)
dotnet run -c Release -- --filter "*CrossLanguage*"      # Cross-language comparison
dotnet run -c Release                                    # All benchmarks
```

### Python

```bash
cd python
pip install h5py                              # optional, for HDF5 comparison
python benchmarks/format_comparison.py        # MeasFlow vs HDF5 (h5py)
python benchmarks/cross_language.py           # Cross-language comparison
```

### C

```bash
# With HDF5 comparison (requires vcpkg: hdf5, lz4, zstd):
cmake -B build -S c -DMEAS_BUILD_BENCHMARKS=ON \
  -DCMAKE_TOOLCHAIN_FILE=$VCPKG_INSTALLATION_ROOT/scripts/buildsystems/vcpkg.cmake
cmake --build build --config Release
./build/bench_format_comparison               # MeasFlow vs HDF5 (libhdf5)
./build/bench_cross_language                  # Cross-language comparison

# Without compression dependencies:
cmake -B build -S c -DMEAS_BUILD_BENCHMARKS=ON \
  -DMEAS_WITH_LZ4=OFF -DMEAS_WITH_ZSTD=OFF
```

## Project Structure

```
csharp/                       C# (.NET 10) implementation
  src/MeasFlow/                 Core library
    Bus/                          Bus data model (CAN, LIN, FlexRay, Ethernet, MOST)
    Format/                       Binary serialization
  tests/MeasFlow.Tests/         40+ tests (roundtrip, bus model, multiplexing, SecOC)
  samples/QuickStart/           Runnable C# quickstart
  benchmarks/                   BenchmarkDotNet performance tests
  tools/MeasFlow.Viewer/        Avalonia-based data viewer
  tools/MeasFlow.DemoGenerator/ Cross-language test data generator
python/                       Python (≥ 3.10) implementation
  measflow/                     Package source
  tests/                        Pytest test suite
  quickstart/quickstart.py      Runnable Python quickstart
c/                            C (C99) implementation
  measflow.c / measflow.h       Single-file library
  tests/                        C unit tests
  quickstart/quickstart.c       Runnable C quickstart
```

## Roadmap

- [ ] Official vcpkg registry publication (upstream [microsoft/vcpkg](https://github.com/microsoft/vcpkg))
- [ ] MATLAB integration
- [ ] Excel plugin

## License

MIT — see [LICENSE](LICENSE)
