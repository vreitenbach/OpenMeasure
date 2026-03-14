# MeasFlow (.meas)

> **⚠️ Prototype:** This project is in prototype stage. The API, file format, and feature set may still change significantly. Not intended for production use.

Open, high-performance measurement data format for .NET. Simple like TDMS, powerful like MDF4.

**MIT License** | **.NET 10** | **Zero dependencies**

## Why?

Existing formats have limitations:
- **TDMS** (NI) — proprietary, limited bus data support
- **HDF5** — complex C-based API, poor .NET interop
- **MDF4** (ASAM) — extremely complex spec, restricted tooling

MeasFlow provides a clean, open alternative with first-class support for automotive bus data (CAN, CAN-FD, LIN, FlexRay, Ethernet) and AUTOSAR concepts (PDU, Container-PDU, Multiplexing, E2E, SecOC).

## Quick Start

```csharp
using MeasFlow;
using MeasFlow.Bus;

// Write measurement data
using var writer = MeasFile.CreateWriter("test.meas");

// Analog sensors
var motor = writer.AddGroup("Motor");
var rpm = motor.AddChannel<float>("RPM");
rpm.Properties["Unit"] = "1/min";
rpm.Write(3000.0f);
rpm.Write(3500.0f);

// CAN bus with structured frame/signal definitions
var can = writer.AddBusGroup("CAN1", new CanBusConfig { BaudRate = 500_000 });

var engineFrame = can.DefineCanFrame("EngineData", frameId: 0x100, payloadLength: 8);
engineFrame.Signals.Add(new SignalDefinition
{
    Name = "EngineRPM",
    StartBit = 0,
    BitLength = 16,
    Factor = 0.25,
    Unit = "rpm",
});

var ts = MeasTimestamp.Now;
can.WriteFrame(ts, 0x100, new byte[] { 0xE0, 0x2E, 0x82, 0x00, 0x00, 0x00, 0x00, 0x00 });
```

```csharp
// Read and decode
using var reader = MeasFile.OpenRead("test.meas");

// Instant statistics without reading data
var stats = reader["Motor"]["RPM"].Statistics;
Console.WriteLine($"RPM: min={stats?.Min} max={stats?.Max} mean={stats?.Mean}");

// Decode signals directly from raw CAN frames
var rpmValues = reader["CAN1"].DecodeSignal("EngineRPM");
Console.WriteLine($"Decoded {rpmValues.Length} RPM values");

// Bus definition preserved
var busDef = reader["CAN1"].BusDefinition!;
var frame = busDef.FindFrame(0x100)!;
Console.WriteLine($"Frame: {frame.Name}, Signals: {frame.Signals.Count}");
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

```bash
cd benchmarks/MeasFlow.Benchmarks
dotnet run -c Release -- --filter "*Write*"    # Write benchmarks
dotnet run -c Release -- --filter "*Read*"     # Read benchmarks
dotnet run -c Release -- --filter "*Size*"     # File size comparison
dotnet run -c Release                          # All benchmarks
```

## Project Structure

```
src/MeasFlow/           Core library
  Bus/                     Bus data model (CAN, LIN, FlexRay, Ethernet, MOST)
  Format/                  Binary serialization (MetadataEncoder, BusMetadataEncoder)
tests/MeasFlow.Tests/   40+ tests (roundtrip, bus model, multiplexing, SecOC)
samples/QuickStart/        Runnable example
benchmarks/                BenchmarkDotNet performance tests
```

## Roadmap

- [ ] Performance benchmarks vs TDMS/HDF5/MDF4
- [ ] Data viewer (signal plots, frame browser)
- [ ] Python implementation (`MEAS-python`)
- [ ] MATLAB integration
- [ ] Excel plugin
- [ ] Compression (LZ4/Zstd)
- [ ] Memory-mapped I/O for large files
- [ ] DBC/ARXML import

## License

MIT — see [LICENSE](LICENSE)
