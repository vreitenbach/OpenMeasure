# Python API Reference

Comprehensive API reference for the Python binding.

## Writer API

### MeasWriter

Create a new measurement file writer.

```python
class MeasWriter:
    def __init__(self, path: str)
```

**Parameters:**

- `path` - File path for the new measurement file

**Context Manager:** Use with `with` statement for automatic cleanup

**Example:**

```python
with meas.Writer("data.meas") as writer:
    # Your code here
    pass
```

### add_group

Add a new group to organize channels.

```python
def add_group(self, name: str) -> MeasGroupWriter
```

**Parameters:**

- `name` - Group name

**Returns:** `MeasGroupWriter` instance

**Example:**

```python
group = writer.add_group("Sensors")
```

### add_channel

Add a channel with data type.

```python
def add_channel(self, name: str, dtype: MeasDataType) -> MeasChannelWriter
```

**Parameters:**

- `name` - Channel name
- `dtype` - Data type (e.g., `MeasDataType.Float32`)

**Returns:** `MeasChannelWriter` instance

**Example:**

```python
temp = group.add_channel("Temperature", meas.DataType.Float32)
pressure = group.add_channel("Pressure", meas.DataType.Float64)
count = group.add_channel("Count", meas.DataType.Int32)
```

### write

Write data to the channel.

```python
def write(self, value: Union[Any, List, np.ndarray])
```

**Parameters:**

- `value` - Single value, list, or NumPy array

**Example:**

```python
# Single value
temp.write(25.5)

# Multiple values
temp.write([25.5, 26.0, 25.8])

# NumPy array
import numpy as np
data = np.array([25.5, 26.0, 25.8], dtype=np.float32)
temp.write(data)
```

### flush

Flush current data to a new segment on disk.

```python
def flush(self)
```

Creates a new data segment and clears the in-memory buffer. Use this periodically during streaming writes to keep memory usage bounded.

**Example:**

```python
while recording:
    batch = read_sensor_data()
    temp.write(batch)
    writer.flush()  # Write segment, clear buffer
```

### properties

Set channel metadata.

```python
channel.properties[key] = value
```

**Example:**

```python
temp.properties["unit"] = "°C"
temp.properties["description"] = "Ambient temperature"
```

---

## Reader API

### MeasReader

Open an existing measurement file for reading.

```python
class MeasReader:
    def __init__(self, path: str)
```

**Parameters:**

- `path` - Path to existing measurement file

**Context Manager:** Use with `with` statement for automatic cleanup

**Example:**

```python
with meas.Reader("data.meas") as reader:
    # Your code here
    pass
```

### Indexing

Access groups and channels by name.

```python
reader[group_name]  # Access group
group[channel_name]  # Access channel
```

**Example:**

```python
group = reader["Sensors"]
channel = group["Temperature"]
# Or chained:
channel = reader["Sensors"]["Temperature"]
```

### read_all

Read all samples as NumPy array.

```python
def read_all(self) -> np.ndarray
```

**Returns:** NumPy array containing all samples

**Example:**

```python
temp = reader["Sensors"]["Temperature"]
data = temp.read_all()

for value in data:
    print(value)
```

### read_timestamps

Read timestamp channel as array of datetime objects.

```python
def read_timestamps(self) -> List[datetime]
```

**Returns:** List of `datetime` objects

**Example:**

```python
timestamps = time_channel.read_timestamps()
```

### statistics

Get pre-computed statistics.

```python
@property
def statistics(self) -> Optional[MeasStatistics]
```

**Returns:** `MeasStatistics` instance or `None`

**Attributes:**

- `count` - Number of samples
- `min` - Minimum value
- `max` - Maximum value
- `mean` - Average value
- `std_dev` - Standard deviation

**Example:**

```python
stats = channel.statistics
if stats:
    print(f"Min: {stats.min}, Max: {stats.max}")
    print(f"Mean: {stats.mean}, StdDev: {stats.std_dev}")
```

### properties

Access channel metadata.

```python
channel.properties[key]
```

**Example:**

```python
unit = channel.properties.get("unit", "")
print(f"Unit: {unit}")
```

### groups

Iterate over all groups in the file.

```python
@property
def groups(self) -> List[MeasGroupReader]
```

**Example:**

```python
for group in reader.groups:
    print(f"Group: {group.name}")
    for channel in group.channels:
        print(f"  Channel: {channel.name}")
```

---

## Data Types

### MeasDataType

Available data types for channels.

| Type | Description | NumPy dtype |
|------|-------------|-------------|
| `MeasDataType.Int8` | 8-bit signed integer | `np.int8` |
| `MeasDataType.Int16` | 16-bit signed integer | `np.int16` |
| `MeasDataType.Int32` | 32-bit signed integer | `np.int32` |
| `MeasDataType.Int64` | 64-bit signed integer | `np.int64` |
| `MeasDataType.UInt8` | 8-bit unsigned integer | `np.uint8` |
| `MeasDataType.UInt16` | 16-bit unsigned integer | `np.uint16` |
| `MeasDataType.UInt32` | 32-bit unsigned integer | `np.uint32` |
| `MeasDataType.UInt64` | 64-bit unsigned integer | `np.uint64` |
| `MeasDataType.Float32` | 32-bit float | `np.float32` |
| `MeasDataType.Float64` | 64-bit float | `np.float64` |
| `MeasDataType.Bool` | Boolean | `np.bool_` |
| `MeasDataType.Timestamp` | Nanosecond timestamp | `np.int64` |
| `MeasDataType.Binary` | Variable-length binary | bytes |

---

## Common Patterns

### Streaming Write

Record continuous data with bounded memory:

```python
with meas.Writer("recording.meas") as writer:
    sensors = writer.add_group("Sensors")
    temp = sensors.add_channel("Temperature", meas.DataType.Float32)

    while recording:
        batch = read_from_sensor(batch_size=10_000)
        temp.write(batch)
        writer.flush()  # Write segment, clear buffer
```

### Read and Analyze

Get statistics and process data with NumPy/Pandas:

```python
with meas.Reader("data.meas") as reader:
    motor = reader["Motor"]

    # Get instant statistics (no data read)
    stats = motor["RPM"].statistics
    print(f"RPM: {stats.min}-{stats.max}, avg={stats.mean}")

    # Read all data for analysis
    rpm_data = motor["RPM"].read_all()
    temp_data = motor["Temperature"].read_all()

    # Process with NumPy/Pandas
    import numpy as np
    correlation = np.corrcoef(rpm_data, temp_data)[0, 1]
    print(f"Correlation: {correlation}")
```

### Iterate Groups and Channels

Explore file structure:

```python
with meas.Reader("data.meas") as reader:
    for group in reader.groups:
        print(f"Group: {group.name}")
        for channel in group.channels:
            stats = channel.statistics
            print(f"  {channel.name}: {stats.count} samples")
```

## See Also

- [C# API Reference](csharp.md)
- [C API Reference](c.md)
- [Streaming Architecture](../guide/streaming.md)
- [Installation](../getting-started/installation.md)
