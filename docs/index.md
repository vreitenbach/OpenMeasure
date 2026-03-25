# MeasFlow

!!! warning "Prototype Stage"
    This project is in prototype stage. The API, file format, and feature set may still change significantly. Not intended for production use.

**Open, high-performance measurement data format with multi-language support**

Simple like TDMS, powerful like MDF4

[![NuGet](https://img.shields.io/nuget/v/MeasFlow)](https://www.nuget.org/packages/MeasFlow)
[![PyPI](https://img.shields.io/pypi/v/measflow)](https://pypi.org/project/measflow/)
[![vcpkg](https://img.shields.io/badge/vcpkg-registry-blue)](https://github.com/vreitenbach/vcpkg-registry)
[![CI](https://github.com/vreitenbach/MeasFlow/actions/workflows/ci.yml/badge.svg)](https://github.com/vreitenbach/MeasFlow/actions/workflows/ci.yml)

[Get Started](getting-started/installation.md){ .md-button .md-button--primary }
[View on GitHub](https://github.com/vreitenbach/MeasFlow){ .md-button }

## Why MeasFlow?

<div class="grid cards" markdown>

-   :rocket: **High Performance**

    ---

    Streaming-first architecture with zero-copy reads and memory-mapped I/O

-   :unlock: **Open & Free**

    ---

    MIT License with minimal dependencies - no vendor lock-in

-   :globe_with_meridians: **Multi-Language**

    ---

    Native support for C#, Python, and C with consistent APIs

-   :red_car: **Automotive Ready**

    ---

    First-class support for CAN, CAN-FD, LIN, FlexRay, and Ethernet bus data

-   :floppy_disk: **Efficient Compression**

    ---

    Optional LZ4/Zstd compression for reduced storage

-   :bar_chart: **Instant Statistics**

    ---

    Min/Max/Mean/StdDev computed during write, available instantly on read

</div>

## Comparison with Existing Formats

| Format | Limitations |
|--------|-------------|
| **TDMS** (NI) | Proprietary, limited bus data support |
| **HDF5** | Complex C-based API, poor .NET interop |
| **MDF4** (ASAM) | Extremely complex spec, restricted tooling |
| **MeasFlow** :white_check_mark: | Clean, open alternative with modern architecture |

## Quick Example

=== "C#"

    ```csharp
    // Write measurement data
    using var writer = MeasFile.CreateWriter("data.meas");
    var group = writer.AddGroup("Sensors");
    var temp = group.AddChannel<float>("Temperature");

    temp.Write(25.5f);
    writer.Flush();

    // Read measurement data
    using var reader = MeasFile.OpenRead("data.meas");
    var channel = reader["Sensors"]["Temperature"];
    var stats = channel.Statistics;
    Console.WriteLine($"Mean: {stats?.Mean}");
    ```

=== "Python"

    ```python
    # Write measurement data
    with meas.Writer("data.meas") as writer:
        group = writer.add_group("Sensors")
        temp = group.add_channel("Temperature", "float32")
        temp.write([25.5])
        writer.flush()

    # Read measurement data
    with meas.Reader("data.meas") as reader:
        channel = reader["Sensors"]["Temperature"]
        stats = channel.statistics
        print(f"Mean: {stats.mean}")
    ```

=== "C"

    ```c
    // Write measurement data
    meas_writer_t* writer = meas_writer_create("data.meas");
    meas_group_t* group = meas_writer_add_group(writer, "Sensors");
    meas_channel_t* temp = meas_group_add_channel(group, "Temperature", MEAS_TYPE_FLOAT32);

    float value = 25.5f;
    meas_channel_write_float32(temp, &value, 1);
    meas_writer_flush(writer);
    meas_writer_destroy(writer);

    // Read measurement data
    meas_reader_t* reader = meas_reader_open("data.meas");
    meas_channel_t* channel = meas_reader_get_channel(reader, "Sensors", "Temperature");
    meas_statistics_t stats = meas_channel_get_statistics(channel);
    printf("Mean: %f\n", stats.mean);
    meas_reader_destroy(reader);
    ```

## Key Features

### Core Capabilities
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

## License

MIT — see [LICENSE](https://github.com/vreitenbach/MeasFlow/blob/main/LICENSE)
