# C# API Reference

Comprehensive API reference for the C# / .NET binding.

## Writer API

### MeasFile.CreateWriter

Create a new measurement file writer.

```csharp
public static MeasFileWriter CreateWriter(string path)
```

**Parameters:**

- `path` - File path for the new measurement file

**Returns:** `MeasFileWriter` instance (implements `IDisposable`)

**Example:**

```csharp
using var writer = MeasFile.CreateWriter("data.meas");
```

### MeasFileWriter.AddGroup

Add a new group to organize channels.

```csharp
public MeasGroupWriter AddGroup(string name)
```

**Parameters:**

- `name` - Group name

**Returns:** `MeasGroupWriter` instance

**Example:**

```csharp
var group = writer.AddGroup("Sensors");
```

### MeasGroupWriter.AddChannel\<T\>

Add a typed channel to the group.

```csharp
public MeasChannelWriter<T> AddChannel<T>(string name)
```

**Type Parameters:**

- `T` - Data type (`float`, `double`, `int`, `long`, `bool`, etc.)

**Parameters:**

- `name` - Channel name

**Returns:** `MeasChannelWriter<T>` instance

**Example:**

```csharp
var temp = group.AddChannel<float>("Temperature");
var pressure = group.AddChannel<double>("Pressure");
var count = group.AddChannel<int>("Count");
```

### MeasChannelWriter.Write

Write data to the channel.

```csharp
public void Write(T value)
public void Write(Span<T> values)
public void Write(ReadOnlySpan<T> values)
```

**Parameters:**

- `value` - Single value to write
- `values` - Span of values for efficient batch writing (zero-copy)

**Example:**

```csharp
// Single value
temp.Write(25.5f);

// Multiple values (efficient)
float[] batch = new float[10000];
temp.Write(batch.AsSpan());
```

### MeasFileWriter.Flush

Flush current data to a new segment on disk.

```csharp
public void Flush()
```

Creates a new data segment and clears the in-memory buffer. Use this periodically during streaming writes to keep memory usage bounded.

**Example:**

```csharp
while (recording)
{
    float[] batch = ReadSensorData();
    temp.Write(batch.AsSpan());
    writer.Flush();  // Write segment, clear buffer
}
```

### MeasFileWriter.Dispose

Finalize and close the file.

```csharp
public void Dispose()
```

Automatically called when using `using` statement. Writes any remaining data and updates file header.

---

## Reader API

### MeasFile.OpenRead

Open an existing measurement file for reading.

```csharp
public static MeasFileReader OpenRead(string path)
```

**Parameters:**

- `path` - Path to existing measurement file

**Returns:** `MeasFileReader` instance (implements `IDisposable`)

**Example:**

```csharp
using var reader = MeasFile.OpenRead("data.meas");
```

### MeasFileReader Indexer

Access a group by name.

```csharp
public MeasGroupReader this[string groupName] { get; }
```

**Parameters:**

- `groupName` - Group name

**Returns:** `MeasGroupReader` instance

**Example:**

```csharp
var group = reader["Sensors"];
```

### MeasGroupReader Indexer

Access a channel by name.

```csharp
public MeasChannelReader this[string channelName] { get; }
```

**Parameters:**

- `channelName` - Channel name

**Returns:** `MeasChannelReader` instance

**Example:**

```csharp
var channel = group["Temperature"];
// Or chained:
var channel = reader["Sensors"]["Temperature"];
```

### MeasChannelReader.ReadAll\<T\>

Read all samples as `ReadOnlySpan<T>`.

```csharp
public ReadOnlySpan<T> ReadAll<T>()
```

**Type Parameters:**

- `T` - Data type matching the channel type

**Returns:** `ReadOnlySpan<T>` containing all samples

**Example:**

```csharp
var temp = reader["Sensors"]["Temperature"];
var data = temp.ReadAll<float>();

foreach (float value in data)
{
    Console.WriteLine(value);
}
```

### MeasChannelReader.ReadChunks\<T\>

Iterate through data chunks (streaming).

```csharp
public IEnumerable<ReadOnlySpan<T>> ReadChunks<T>()
```

**Type Parameters:**

- `T` - Data type matching the channel type

**Returns:** Enumerable of `ReadOnlySpan<T>` chunks

**Example:**

```csharp
// Only one chunk in memory at a time
foreach (var chunk in channel.ReadChunks<float>())
{
    Process(chunk);
}
```

### MeasChannelReader.Statistics

Get pre-computed statistics (instant, no I/O).

```csharp
public MeasStatistics? Statistics { get; }
```

**Returns:** `MeasStatistics` instance or `null` if no statistics available

**Properties:**

- `Count` - Number of samples
- `Min` - Minimum value
- `Max` - Maximum value
- `Mean` - Average value
- `StdDev` - Standard deviation

**Example:**

```csharp
var stats = channel.Statistics;
if (stats != null)
{
    Console.WriteLine($"Min: {stats.Min}, Max: {stats.Max}");
    Console.WriteLine($"Mean: {stats.Mean}, StdDev: {stats.StdDev}");
}
```

### MeasChannelReader.Properties

Access channel metadata.

```csharp
public IReadOnlyDictionary<string, object> Properties { get; }
```

**Example:**

```csharp
if (channel.Properties.TryGetValue("unit", out var unit))
{
    Console.WriteLine($"Unit: {unit}");
}
```

---

## Bus Data API

For automotive bus data (CAN, LIN, FlexRay, Ethernet).

### MeasFileWriter.AddBusGroup

Add a bus channel.

```csharp
public MeasBusGroupWriter AddBusGroup(string name, BusConfig config)
```

**Parameters:**

- `name` - Group name
- `config` - Bus configuration (CAN, LIN, FlexRay, Ethernet)

**Returns:** `MeasBusGroupWriter` instance

**Example:**

```csharp
var canConfig = new CanBusConfig { BusType = CanBusType.CanFd };
var busGroup = writer.AddBusGroup("CAN1", canConfig);
```

### MeasBusGroupWriter.DefineCanFrame

Define a CAN frame with signals.

```csharp
public CanFrameDefinition DefineCanFrame(string name, uint id, byte length)
```

**Parameters:**

- `name` - Frame name
- `id` - CAN identifier
- `length` - Payload length in bytes

**Returns:** `CanFrameDefinition` instance

**Example:**

```csharp
var engineFrame = busGroup.DefineCanFrame("Engine", 0x123, 8);
engineFrame.Signals.Add(new SignalDefinition
{
    Name = "RPM",
    StartBit = 0,
    BitLength = 16,
    Factor = 0.25,
    Offset = 0,
    Unit = "rpm"
});
```

### MeasBusGroupWriter.WriteFrame

Write raw frame bytes with timestamp.

```csharp
public void WriteFrame(MeasTimestamp timestamp, uint id, ReadOnlySpan<byte> data)
```

**Parameters:**

- `timestamp` - Nanosecond-precision timestamp
- `id` - Frame identifier
- `data` - Frame payload bytes

**Example:**

```csharp
byte[] payload = new byte[] { 0x12, 0x34, 0x56, 0x78 };
busGroup.WriteFrame(MeasTimestamp.Now, 0x123, payload);
```

### MeasBusGroupReader.DecodeSignal

Decode and extract signal values from frames.

```csharp
public ReadOnlySpan<double> DecodeSignal(string signalName)
```

**Parameters:**

- `signalName` - Signal name

**Returns:** Decoded signal values (with factor/offset applied)

**Example:**

```csharp
var rpm = busGroup.DecodeSignal("RPM");
Console.WriteLine($"Average RPM: {rpm.ToArray().Average()}");
```

---

## Common Patterns

### Streaming Write

Record continuous data with bounded memory:

```csharp
using var writer = MeasFile.CreateWriter("recording.meas");
var sensors = writer.AddGroup("Sensors");
var temp = sensors.AddChannel<float>("Temperature");

while (recording)
{
    float[] batch = ReadFromSensor(batchSize: 10_000);
    temp.Write(batch.AsSpan());
    writer.Flush();  // Write segment, clear buffer
}
```

### Streaming Read

Process large files chunk by chunk:

```csharp
using var reader = MeasFile.OpenRead("recording.meas");
var channel = reader["Sensors"]["Temperature"];

foreach (var chunk in channel.ReadChunks<float>())
{
    // Only one chunk in memory at a time
    ProcessChunk(chunk);
}
```

### Instant Statistics

Get statistics without reading data:

```csharp
using var reader = MeasFile.OpenRead("data.meas");
var channel = reader["Sensors"]["Temperature"];

var stats = channel.Statistics;
Console.WriteLine($"Range: {stats?.Min} - {stats?.Max}");
Console.WriteLine($"Mean: {stats?.Mean}");

// No data was read!
```

## See Also

- [Python API Reference](python.md)
- [C API Reference](c.md)
- [Streaming Architecture](../guide/streaming.md)
- [Bus Data Support](../guide/bus-data.md)
