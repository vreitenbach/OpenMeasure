# MeasFlow — C# Implementation

A .NET 10 implementation of the MeasFlow (`.meas`) binary measurement format.

**MIT License** | **.NET 10** | **Zero dependencies**

## Building

```sh
cd csharp/
dotnet build MeasFlow.slnx
```

## Quick Start

Run the quickstart sample to write and read back a demo measurement file:

```sh
cd csharp/samples/QuickStart
dotnet run
```

See [`samples/QuickStart/Program.cs`](samples/QuickStart/Program.cs) for the full example.

### Writing

```csharp
using MeasFlow;
using MeasFlow.Bus;

using var writer = MeasFile.CreateWriter("data.meas");

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

### Reading

```csharp
using var reader = MeasFile.OpenRead("data.meas");

// Instant statistics without reading data
var stats = reader["Motor"]["RPM"].Statistics;
Console.WriteLine($"RPM: min={stats?.Min} max={stats?.Max} mean={stats?.Mean}");

// Decode signals directly from raw CAN frames
var rpmValues = reader["CAN1"].DecodeSignal("EngineRPM");
Console.WriteLine($"Decoded {rpmValues.Length} RPM values");
```

## Tests

```sh
cd csharp/
dotnet test MeasFlow.slnx
```

## Benchmarks

```sh
cd csharp/benchmarks/MeasFlow.Benchmarks
dotnet run -c Release -- --filter "*Write*"    # Write benchmarks
dotnet run -c Release -- --filter "*Read*"     # Read benchmarks
dotnet run -c Release -- --filter "*Size*"     # File size comparison
dotnet run -c Release                          # All benchmarks
```

## Project Structure

```
src/MeasFlow/           Core library
  Bus/                    Bus data model (CAN, LIN, FlexRay, Ethernet, MOST)
  Format/                 Binary serialization (MetadataEncoder, BusMetadataEncoder)
tests/MeasFlow.Tests/   40+ tests (roundtrip, bus model, multiplexing, SecOC)
samples/QuickStart/     Runnable quickstart example
benchmarks/             BenchmarkDotNet performance tests
tools/MeasFlow.Viewer/  Avalonia-based data viewer
```

> **Full specification**: See [`../SPECIFICATION.md`](../SPECIFICATION.md) for the complete binary format.
