# Streaming Architecture

MeasFlow is **streaming-first** — the format is designed so data can be written and read incrementally without holding the entire file in memory.

## Design Principles

1. **Bounded Memory**: Memory usage stays constant regardless of file size
2. **Forward-Only**: No seeking backwards required during write
3. **Incremental Flush**: Data segments written as they're ready
4. **Readable During Write**: Partial files can be read while writing continues

## File Layout

MeasFlow uses a segment-linked file layout:

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

### Segment Linking

- Segments form a **forward-linked list**
- Each segment contains a `NextOffset` pointer to the next segment
- Readers scan sequentially—no random access needed
- Last segment has `NextOffset = 0`

## Streaming Writes

Writers maintain a single in-memory buffer that gets flushed to disk periodically.

### Write Workflow

1. Application writes data to channels
2. Data accumulates in memory buffer
3. Application calls `Flush()`
4. Buffer written as new data segment
5. Buffer cleared
6. Repeat from step 1

### Memory Usage

Memory usage is **independent of recording duration**:

=== "C#"

    ```csharp
    using var writer = MeasFile.CreateWriter("long_recording.meas");
    var group = writer.AddGroup("Sensors");
    var temp = group.AddChannel<float>("Temperature");

    // Record for hours—memory stays constant
    while (recording)
    {
        float[] batch = ReadFromSensor(batchSize: 10_000);
        temp.Write(batch.AsSpan());
        writer.Flush();  // Memory cleared here
    }
    ```

=== "Python"

    ```python
    with meas.Writer("long_recording.meas") as writer:
        group = writer.add_group("Sensors")
        temp = group.add_channel("Temperature", meas.DataType.Float32)

        # Record for hours—memory stays constant
        while recording:
            batch = read_from_sensor(batch_size=10_000)
            temp.write(batch)
            writer.flush()  # Memory cleared here
    ```

=== "C"

    ```c
    meas_writer_t* w = meas_writer_open("long_recording.meas");
    meas_group_t* g = meas_writer_add_group(w, "Sensors");
    meas_channel_t* temp = meas_group_add_channel(g, "Temperature", MEAS_FLOAT32);

    // Record for hours—memory stays constant
    while (recording) {
        float batch[10000];
        read_from_sensor(batch, 10000);
        meas_channel_write_f32(temp, batch, 10000);
        meas_writer_flush(w);  // Memory cleared here
    }
    meas_writer_close(w);
    ```

## Streaming Reads

Readers can process files **chunk-by-chunk** without loading the entire dataset.

### Chunk-Based Reading

=== "C#"

    ```csharp
    using var reader = MeasFile.OpenRead("large_file.meas");
    var channel = reader["Sensors"]["Temperature"];

    // Process one chunk at a time
    foreach (var chunk in channel.ReadChunks<float>())
    {
        // Only this chunk in memory
        ProcessChunk(chunk);
        // Chunk deallocated when next chunk loads
    }
    ```

=== "Python"

    ```python
    with meas.Reader("large_file.meas") as reader:
        channel = reader["Sensors"]["Temperature"]

        # Read in manageable chunks
        chunk_size = 100_000
        offset = 0
        while offset < channel.sample_count:
            chunk = channel.read_range(offset, chunk_size)
            process_chunk(chunk)
            offset += chunk_size
    ```

=== "C"

    ```c
    const meas_channel_data_t* ch = /* ... */;

    // Process in chunks
    size_t chunk_size = 100000;
    float* buf = malloc(chunk_size * sizeof(float));

    size_t offset = 0;
    while (offset < ch->sample_count) {
        size_t to_read = min(chunk_size, ch->sample_count - offset);
        size_t read = meas_channel_read_f32(ch, buf, to_read);
        process_chunk(buf, read);
        offset += read;
    }

    free(buf);
    ```

## Instant Statistics

Statistics are **computed during write** and stored in metadata. Reading statistics requires **zero I/O**:

=== "C#"

    ```csharp
    using var reader = MeasFile.OpenRead("data.meas");
    var channel = reader["Sensors"]["Temperature"];

    var stats = channel.Statistics;  // No data read!
    Console.WriteLine($"Range: {stats?.Min} - {stats?.Max}");
    Console.WriteLine($"Mean: {stats?.Mean}, StdDev: {stats?.StdDev}");
    ```

=== "Python"

    ```python
    with meas.Reader("data.meas") as reader:
        channel = reader["Sensors"]["Temperature"]

        stats = channel.statistics  # No data read!
        print(f"Range: {stats.min} - {stats.max}")
        print(f"Mean: {stats.mean}, StdDev: {stats.std_dev}")
    ```

=== "C"

    ```c
    const meas_channel_data_t* ch = /* ... */;

    if (ch->has_stats) {  // No data read!
        printf("Range: %.2f - %.2f\n", ch->stats.min, ch->stats.max);
        printf("Mean: %.2f, StdDev: %.2f\n", ch->stats.mean, ch->stats.std_dev);
    }
    ```

## Live File Reading

Partial files (writer still open) can be read up to the last complete segment.

### Use Case: Real-Time Monitoring

**Writer process:**

```csharp
// Process A: Writing
using var writer = MeasFile.CreateWriter("live.meas");
var temp = writer.AddGroup("Sensors").AddChannel<float>("Temperature");

while (true)
{
    temp.Write(ReadSensor());
    writer.Flush();  // Segment immediately available
    Thread.Sleep(100);
}
```

**Reader process (parallel):**

```csharp
// Process B: Reading (while A is still writing)
using var reader = MeasFile.OpenRead("live.meas");
var channel = reader["Sensors"]["Temperature"];

// Reads all data written so far (up to last flush)
var data = channel.ReadAll<float>();
```

## Performance Characteristics

### Write Performance

- **Throughput**: ~500 MB/s uncompressed (SSD)
- **Memory**: Constant (configurable buffer size)
- **Latency**: Flush completes in ~10ms for 1MB buffer

### Read Performance

- **Sequential read**: ~1 GB/s (memory-mapped I/O)
- **Statistics read**: ~1 μs (metadata only)
- **Chunk iteration**: Zero-copy, minimal overhead

## Comparison with Other Formats

| Format | Streaming Write | Streaming Read | Memory Usage |
|--------|----------------|----------------|--------------|
| **MeasFlow** | ✅ Native | ✅ Native | Constant |
| **HDF5** | ⚠️ Requires chunking | ✅ With chunked storage | Variable |
| **TDMS** | ✅ Native | ⚠️ Limited | Variable |
| **MDF4** | ❌ Finalize required | ⚠️ Complex | High |

## Best Practices

### When to Flush

- **Time-based**: Flush every N seconds (e.g., 1-10s)
- **Size-based**: Flush when buffer reaches N MB (e.g., 10-100 MB)
- **Event-based**: Flush on significant events (test completion, error, etc.)

### Chunk Size Selection

- **Small files** (< 100 MB): Flush less frequently (larger chunks)
- **Large files** (> 1 GB): Flush more frequently (smaller chunks)
- **Real-time monitoring**: Flush frequently (small chunks, low latency)

### Reading Strategies

- **Full analysis**: Use `ReadAll()` if file fits in memory
- **Large files**: Use `ReadChunks()` for streaming processing
- **Preview**: Read statistics only, then read data if needed

## See Also

- [File Format Specification](file-format.md)
- [Bus Data Support](bus-data.md)
- [API Reference](../api/csharp.md)
