# File Format

Complete binary format specification for .meas files.

## Overview

| Layer | Content | Size |
|-------|---------|------|
| **Header** | Magic, version, metadata | 64 bytes (fixed) |
| **Metadata Segment** | Groups, channels, properties, bus definitions | Variable |
| **Data Segments** (repeated) | Chunked channel data | Variable |

## File Header (64 bytes)

```
Offset | Size | Field              | Description
-------|------|--------------------|---------------------------------
0      | 5    | Magic              | "MEAS\0" (ASCII + null terminator)
5      | 1    | MajorVersion       | Format major version
6      | 1    | MinorVersion       | Format minor version
7      | 1    | Flags              | Header flags
8      | 8    | MetadataOffset     | Offset to first metadata segment
16     | 4    | SegmentCount       | Total number of data segments
20     | 4    | Reserved           | Reserved for future use
24     | 16   | FileGuid           | Unique file identifier (GUID)
40     | 8    | CreationTimestamp  | File creation time (Unix nanoseconds)
48     | 16   | Reserved           | Reserved for future use
```

### Magic Number

`MEAS\0` (0x4D 0x45 0x41 0x53 0x00) identifies the file format.

### Version

- **Current**: 1.0
- **MajorVersion**: Breaking changes
- **MinorVersion**: Backward-compatible additions

### Flags

| Bit | Meaning |
|-----|---------|
| 0   | Extended metadata (file-level properties) |
| 1-7 | Reserved |

## Metadata Segment

Contains structural information and definitions.

### Segment Header

```
Offset | Size | Field        | Description
-------|------|--------------|---------------------------
0      | 8    | SegmentType  | 0x01 for metadata
8      | 8    | NextOffset   | Offset to next segment (0 if last)
16     | 8    | DataSize     | Size of segment data
```

### Metadata Content

#### When Flags bit 0 is **not** set:

```
Offset | Size | Field              | Description
-------|------|--------------------|---------------------------
0      | 4    | GroupCount         | Number of groups
4      | ...  | Groups[]           | Group definitions
```

#### When Flags bit 0 **is** set (extended metadata):

```
Offset | Size | Field              | Description
-------|------|--------------------|---------------------------
0      | 2    | MetadataMajor      | Metadata schema major version
2      | 2    | MetadataMinor      | Metadata schema minor version
4      | 4    | PropertyCount      | Number of file-level properties
8      | ...  | Properties[]       | File-level property key-value pairs
...    | 4    | GroupCount         | Number of groups
...    | ...  | Groups[]           | Group definitions
```

### Group Definition

```
Offset | Size | Field          | Description
-------|------|----------------|---------------------------
0      | 2    | NameLength     | Length of group name
2      | N    | Name           | UTF-8 group name
N+2    | 4    | ChannelCount   | Number of channels in group
N+6    | 4    | PropertyCount  | Number of group properties
N+10   | ...  | Properties[]   | Group properties
...    | ...  | Channels[]     | Channel definitions
```

### Channel Definition

```
Offset | Size | Field          | Description
-------|------|----------------|---------------------------
0      | 2    | NameLength     | Length of channel name
2      | N    | Name           | UTF-8 channel name
N+2    | 1    | DataType       | Data type code (see below)
N+3    | 8    | SampleCount    | Total number of samples
N+11   | 1    | HasStatistics  | 1 if statistics present, 0 otherwise
N+12   | 40   | Statistics     | Min/Max/Mean/StdDev (if HasStatistics=1)
...    | 4    | PropertyCount  | Number of channel properties
...    | ...  | Properties[]   | Channel properties
```

### Data Type Codes

| Code | Type | Size (bytes) |
|------|------|--------------|
| 0x01 | Int8 | 1 |
| 0x02 | Int16 | 2 |
| 0x03 | Int32 | 4 |
| 0x04 | Int64 | 8 |
| 0x11 | UInt8 | 1 |
| 0x12 | UInt16 | 2 |
| 0x13 | UInt32 | 4 |
| 0x14 | UInt64 | 8 |
| 0x21 | Float32 | 4 |
| 0x22 | Float64 | 8 |
| 0x30 | Bool | 1 |
| 0x40 | Timestamp | 8 (nanoseconds) |
| 0x50 | Binary | Variable |

### Statistics Structure (40 bytes)

```
Offset | Size | Field    | Description
-------|------|----------|---------------------------
0      | 8    | Min      | Minimum value (float64)
8      | 8    | Max      | Maximum value (float64)
16     | 8    | Mean     | Average value (float64)
24     | 8    | StdDev   | Standard deviation (float64)
32     | 8    | Reserved | Reserved for future use
```

## Data Segment

Contains actual measurement data.

### Segment Header

```
Offset | Size | Field        | Description
-------|------|--------------|---------------------------
0      | 8    | SegmentType  | 0x02 for data
8      | 8    | NextOffset   | Offset to next segment (0 if last)
16     | 8    | DataSize     | Total size of data in this segment
```

### Channel Data Chunks

For each channel in order:

#### Fixed-Size Types (Int, Float, Bool, Timestamp)

```
Offset | Size | Field          | Description
-------|------|----------------|---------------------------
0      | 4    | ChannelIndex   | Index of channel (0-based)
4      | 4    | SampleCount    | Number of samples in this chunk
8      | N×S  | Samples        | Raw sample data (N samples × S bytes each)
```

#### Variable-Length Types (Binary)

```
Offset | Size | Field          | Description
-------|------|----------------|---------------------------
0      | 4    | ChannelIndex   | Index of channel (0-based)
4      | 4    | FrameCount     | Number of frames in this chunk
8      | ...  | Frames[]       | Length-prefixed frames
```

Each frame:

```
Offset | Size | Field          | Description
-------|------|----------------|---------------------------
0      | 4    | FrameLength    | Length of this frame
4      | N    | FrameData      | Raw frame bytes
```

## Compression (Optional)

Data segments can be compressed:

### Compressed Segment Header

```
Offset | Size | Field              | Description
-------|------|--------------------|---------------------------
0      | 8    | SegmentType        | 0x82 for compressed data
8      | 8    | NextOffset         | Offset to next segment
16     | 8    | CompressedSize     | Size of compressed data
24     | 8    | UncompressedSize   | Original data size
32     | 1    | CompressionType    | 0x01=LZ4, 0x02=Zstd
33     | ...  | CompressedData     | Compressed segment data
```

### Compression Types

| Code | Algorithm | Typical Ratio |
|------|-----------|---------------|
| 0x01 | LZ4 | 2-3× (fast) |
| 0x02 | Zstd | 3-5× (better compression) |

## Endianness

All multi-byte integers are **little-endian**.

## Alignment

No special alignment requirements. Fields are packed sequentially.

## Validation

### Required Checks

1. **Magic number**: Must be `MEAS\0`
2. **Version**: Major version must match implementation
3. **Segment chain**: All `NextOffset` values must be valid or 0
4. **Sample counts**: Sum of chunk sample counts must equal channel `SampleCount`

### Recommended Checks

1. **CRC/Checksum**: Not currently included (future addition)
2. **GUID uniqueness**: Verify file GUID is unique
3. **Timestamp ordering**: Verify timestamps are monotonically increasing

## Conformance Levels

### Level 1: Basic

- Read/write fixed-size channels (Int, Float)
- Read metadata
- No compression support

### Level 2: Extended

- Variable-length channels (Binary for bus data)
- Statistics computation
- Properties

### Level 3: Full

- All data types
- Compression (LZ4, Zstd)
- Bus data definitions (CAN, LIN, etc.)
- E2E protection, SecOC

## Future Extensions

Reserved fields allow for backward-compatible additions:

- **File-level encryption**
- **Digital signatures**
- **Embedded thumbnails/previews**
- **Advanced compression codecs**

## See Also

- [Streaming Architecture](streaming.md)
- [Bus Data Support](bus-data.md)
- [Full Specification (GitHub)](https://github.com/vreitenbach/MeasFlow/blob/main/SPECIFICATION.md)
