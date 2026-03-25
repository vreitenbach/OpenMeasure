# C API Reference

Comprehensive API reference for the C binding.

## Writer API

### meas_writer_open

Create a new measurement file writer.

```c
meas_writer_t* meas_writer_open(const char* path);
```

**Parameters:**

- `path` - File path for the new measurement file

**Returns:** `meas_writer_t*` pointer or `NULL` on error

**Example:**

```c
meas_writer_t* writer = meas_writer_open("data.meas");
if (!writer) {
    fprintf(stderr, "Failed to create writer\n");
    return 1;
}
```

### meas_writer_add_group

Add a new group to organize channels.

```c
meas_group_t* meas_writer_add_group(meas_writer_t* writer, const char* name);
```

**Parameters:**

- `writer` - Writer instance
- `name` - Group name

**Returns:** `meas_group_t*` pointer or `NULL` on error

**Example:**

```c
meas_group_t* group = meas_writer_add_group(writer, "Sensors");
```

### meas_group_add_channel

Add a channel with data type.

```c
meas_channel_t* meas_group_add_channel(meas_group_t* group, const char* name, meas_data_type_t type);
```

**Parameters:**

- `group` - Group instance
- `name` - Channel name
- `type` - Data type constant (e.g., `MEAS_FLOAT32`)

**Returns:** `meas_channel_t*` pointer or `NULL` on error

**Example:**

```c
meas_channel_t* temp = meas_group_add_channel(group, "Temperature", MEAS_FLOAT32);
meas_channel_t* count = meas_group_add_channel(group, "Count", MEAS_INT32);
```

### meas_channel_write_*

Write data to the channel (type-specific functions).

```c
int meas_channel_write_f32(meas_channel_t* ch, const float* data, size_t count);
int meas_channel_write_f64(meas_channel_t* ch, const double* data, size_t count);
int meas_channel_write_i32(meas_channel_t* ch, const int32_t* data, size_t count);
int meas_channel_write_i64(meas_channel_t* ch, const int64_t* data, size_t count);
// ... and more for other types
```

**Parameters:**

- `ch` - Channel instance
- `data` - Pointer to data array
- `count` - Number of elements to write

**Returns:** 0 on success, non-zero on error

**Example:**

```c
float values[] = {25.5f, 26.0f, 25.8f};
meas_channel_write_f32(temp, values, 3);
```

### meas_channel_write_frame

Write variable-length binary frame.

```c
int meas_channel_write_frame(meas_channel_t* ch, const void* data, size_t len);
```

**Parameters:**

- `ch` - Channel instance (must be `MEAS_BINARY` type)
- `data` - Pointer to frame data
- `len` - Frame length in bytes

**Returns:** 0 on success, non-zero on error

**Example:**

```c
uint8_t frame[] = {0x12, 0x34, 0x56, 0x78};
meas_channel_write_frame(bus_channel, frame, sizeof(frame));
```

### meas_writer_flush

Flush current data to a new segment.

```c
void meas_writer_flush(meas_writer_t* writer);
```

Creates a new data segment and clears the in-memory buffer.

**Example:**

```c
while (recording) {
    float batch[10000];
    read_from_sensor(batch, 10000);
    meas_channel_write_f32(temp, batch, 10000);
    meas_writer_flush(writer);
}
```

### meas_writer_close

Finalize and close the file.

```c
void meas_writer_close(meas_writer_t* writer);
```

Writes any remaining data, updates header, and frees resources.

**Example:**

```c
meas_writer_close(writer);
```

---

## Reader API

### meas_reader_open

Open an existing measurement file.

```c
meas_reader_t* meas_reader_open(const char* path);
```

**Parameters:**

- `path` - Path to existing measurement file

**Returns:** `meas_reader_t*` pointer or `NULL` on error

**Example:**

```c
meas_reader_t* reader = meas_reader_open("data.meas");
if (!reader) {
    fprintf(stderr, "Failed to open file\n");
    return 1;
}
```

### meas_reader_group_by_name

Get group by name.

```c
const meas_group_data_t* meas_reader_group_by_name(meas_reader_t* reader, const char* name);
```

**Parameters:**

- `reader` - Reader instance
- `name` - Group name

**Returns:** `const meas_group_data_t*` pointer or `NULL` if not found

**Example:**

```c
const meas_group_data_t* group = meas_reader_group_by_name(reader, "Sensors");
```

### meas_group_channel_by_name

Get channel by name.

```c
const meas_channel_data_t* meas_group_channel_by_name(const meas_group_data_t* group, const char* name);
```

**Parameters:**

- `group` - Group instance
- `name` - Channel name

**Returns:** `const meas_channel_data_t*` pointer or `NULL` if not found

**Example:**

```c
const meas_channel_data_t* ch = meas_group_channel_by_name(group, "Temperature");
```

### meas_channel_read_*

Read data into buffer (type-specific functions).

```c
size_t meas_channel_read_f32(const meas_channel_data_t* ch, float* buf, size_t count);
size_t meas_channel_read_f64(const meas_channel_data_t* ch, double* buf, size_t count);
size_t meas_channel_read_i32(const meas_channel_data_t* ch, int32_t* buf, size_t count);
// ... and more for other types
```

**Parameters:**

- `ch` - Channel instance
- `buf` - Buffer to read into
- `count` - Maximum number of elements to read

**Returns:** Number of elements actually read

**Example:**

```c
float data[1000];
size_t read = meas_channel_read_f32(ch, data, 1000);
printf("Read %zu values\n", read);
```

### meas_channel_next_frame

Iterate through binary frames.

```c
int meas_channel_next_frame(const meas_channel_data_t* ch, meas_frame_iter_t* state, const void** data, size_t* len);
```

**Parameters:**

- `ch` - Channel instance (must be `MEAS_BINARY` type)
- `state` - Iterator state (initialize to 0)
- `data` - Output pointer to frame data
- `len` - Output frame length

**Returns:** 1 if frame read, 0 if no more frames, -1 on error

**Example:**

```c
meas_frame_iter_t iter = 0;
const void* frame_data;
size_t frame_len;

while (meas_channel_next_frame(ch, &iter, &frame_data, &frame_len) == 1) {
    printf("Frame: %zu bytes\n", frame_len);
    // Process frame_data
}
```

### Channel Data Structure

Channel information is available through `meas_channel_data_t`:

```c
typedef struct {
    const char* name;
    meas_data_type_t data_type;
    uint64_t sample_count;
    bool has_stats;
    meas_statistics_t stats;
    // ... other fields
} meas_channel_data_t;
```

**Fields:**

- `name` - Channel name
- `data_type` - Data type constant
- `sample_count` - Total number of samples
- `has_stats` - Whether statistics are available
- `stats` - Pre-computed statistics

**Example:**

```c
if (ch->has_stats) {
    printf("Min: %.2f, Max: %.2f\n", ch->stats.min, ch->stats.max);
    printf("Mean: %.2f, StdDev: %.2f\n", ch->stats.mean, ch->stats.std_dev);
}

printf("Total samples: %llu\n", (unsigned long long)ch->sample_count);
```

### meas_reader_close

Close the reader and free resources.

```c
void meas_reader_close(meas_reader_t* reader);
```

**Example:**

```c
meas_reader_close(reader);
```

---

## Data Type Constants

| Constant | C Type | Bytes |
|----------|--------|-------|
| `MEAS_INT8` | `int8_t` | 1 |
| `MEAS_INT16` | `int16_t` | 2 |
| `MEAS_INT32` | `int32_t` | 4 |
| `MEAS_INT64` | `int64_t` | 8 |
| `MEAS_UINT8` | `uint8_t` | 1 |
| `MEAS_UINT16` | `uint16_t` | 2 |
| `MEAS_UINT32` | `uint32_t` | 4 |
| `MEAS_UINT64` | `uint64_t` | 8 |
| `MEAS_FLOAT32` | `float` | 4 |
| `MEAS_FLOAT64` | `double` | 8 |
| `MEAS_BOOL` | `uint8_t` (0/1) | 1 |
| `MEAS_TIMESTAMP` | `int64_t` (nanoseconds) | 8 |
| `MEAS_BINARY` | Variable-length frames | — |

---

## Common Patterns

### Error Handling

Always check return values:

```c
meas_reader_t* r = meas_reader_open("data.meas");
if (!r) {
    fprintf(stderr, "Failed to open file\n");
    return 1;
}

const meas_group_data_t* g = meas_reader_group_by_name(r, "Motor");
if (!g) {
    fprintf(stderr, "Group 'Motor' not found\n");
    meas_reader_close(r);
    return 1;
}

const meas_channel_data_t* rpm = meas_group_channel_by_name(g, "RPM");
if (rpm && rpm->has_stats) {
    printf("RPM range: %.1f - %.1f\n", rpm->stats.min, rpm->stats.max);
}

meas_reader_close(r);
```

### Streaming Write

Record continuous data with bounded memory:

```c
meas_writer_t* w = meas_writer_open("recording.meas");
meas_group_t* g = meas_writer_add_group(w, "Sensors");
meas_channel_t* temp = meas_group_add_channel(g, "Temperature", MEAS_FLOAT32);

while (recording) {
    float batch[10000];
    read_from_sensor(batch, 10000);
    meas_channel_write_f32(temp, batch, 10000);
    meas_writer_flush(w);
}

meas_writer_close(w);
```

### Read All Data

Read complete channel:

```c
const meas_channel_data_t* ch = /* ... */;

// Allocate buffer
float* data = malloc(ch->sample_count * sizeof(float));

// Read all
size_t read = meas_channel_read_f32(ch, data, ch->sample_count);

// Process
for (size_t i = 0; i < read; i++) {
    printf("%.2f\n", data[i]);
}

free(data);
```

## See Also

- [C# API Reference](csharp.md)
- [Python API Reference](python.md)
- [Streaming Architecture](../guide/streaming.md)
- [Installation](../getting-started/installation.md)
