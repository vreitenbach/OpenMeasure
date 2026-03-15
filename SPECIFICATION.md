# MeasFlow (.meas) Binary Format Specification

**Version 1.0** — March 2026

This document specifies the binary file format for MeasFlow (.meas) files independently of any implementation. Any conforming reader/writer in any language (C#, C, Python, Rust, MATLAB) MUST follow this specification.

---

## Table of Contents

1. [Design Principles](#1-design-principles)
2. [Byte Order](#2-byte-order)
3. [File Structure Overview](#3-file-structure-overview)
4. [File Header (64 bytes)](#4-file-header-64-bytes)
5. [Segment Structure](#5-segment-structure)
6. [Metadata Segment](#6-metadata-segment)
7. [Data Segment](#7-data-segment)
8. [Data Types](#8-data-types)
9. [Property System](#9-property-system)
10. [Bus Metadata (AUTOSAR)](#10-bus-metadata-autosar)
11. [Raw Frame Wire Formats](#11-raw-frame-wire-formats)
12. [Streaming Model](#12-streaming-model)
13. [Channel Statistics](#13-channel-statistics)
14. [Conformance Requirements](#14-conformance-requirements)

---

## 1. Design Principles

- **Streaming-first**: Data can be written and read incrementally without buffering the entire file in memory.
- **Segment-linked**: Segments form a forward-linked list enabling sequential scanning without random access.
- **Zero-copy friendly**: Fixed-size data is stored as raw little-endian bytes, directly mappable to memory.
- **Self-describing**: All metadata (groups, channels, properties, bus definitions) is embedded in the file.
- **Extensible**: New segment types, data types, and bus types can be added without breaking existing readers.

---

## 2. Byte Order

All multi-byte integers and floating-point values are stored in **little-endian** byte order.

All strings are encoded as **UTF-8** with a 4-byte (int32) length prefix (byte count, not character count).

```
String := [int32: byteLength] [bytes: UTF-8 data]
```

---

## 3. File Structure Overview

An .meas file consists of a fixed-size header followed by a chain of segments:

```
┌──────────────────────────────────┐  Offset 0
│          File Header (64 B)      │
├──────────────────────────────────┤  Offset 64
│    Segment Header (32 B)         │
│    Segment Content (variable)    │
│          ↓ NextSegmentOffset     │
├──────────────────────────────────┤
│    Segment Header (32 B)         │
│    Segment Content (variable)    │
│          ↓ NextSegmentOffset     │
├──────────────────────────────────┤
│           ...                    │
│    (more segments)               │
├──────────────────────────────────┤
│    Last Segment Header (32 B)    │
│    Last Segment Content          │
│    NextSegmentOffset = end-of-file│
└──────────────────────────────────┘
```

**Invariant**: The first segment MUST be a Metadata segment. Data segments follow.

---

## 4. File Header (64 bytes)

| Offset | Size | Type    | Field              | Description                                      |
|--------|------|---------|--------------------|--------------------------------------------------|
| 0      | 4    | uint32  | Magic              | `0x5341454D` = ASCII `"MEAS\0"` (LE)              |
| 4      | 2    | uint16  | Version            | Format version. Currently `1`.                    |
| 6      | 2    | uint16  | Flags              | Reserved bit flags. Must be `0` for version 1.   |
| 8      | 8    | int64   | FirstSegmentOffset | Absolute byte offset to the first segment. Usually `64`. |
| 16     | 8    | int64   | IndexOffset        | Reserved. Must be `0` for version 1.             |
| 24     | 8    | int64   | SegmentCount       | Total number of segments written (updated on close). |
| 32     | 16   | GUID    | FileId             | Unique file identifier (128-bit, RFC 4122).      |
| 48     | 8    | int64   | CreatedAtNanos     | File creation time as nanoseconds since Unix epoch. |
| 56     | 8    | int64   | Reserved           | Must be `0`. Padding to exactly 64 bytes.         |

**Magic byte pattern** (hex): `4D 45 41 53`

**Version negotiation**: Readers MUST reject files with `Version > 1` unless they understand the newer version. Readers MUST reject files where `Magic ≠ 0x5341454D`.

**SegmentCount**: This field is written as `0` initially and patched to the final count when the writer closes. A streaming reader that encounters `SegmentCount = 0` SHOULD walk the segment chain using `NextSegmentOffset` until reaching end-of-file.

---

## 5. Segment Structure

Every segment starts with a 32-byte header:

| Offset | Size | Type   | Field              | Description                                      |
|--------|------|--------|--------------------|--------------------------------------------------|
| 0      | 4    | int32  | Type               | Segment type (see below).                        |
| 4      | 4    | int32  | Flags              | Compression algorithm in bits 0–3. See §4a.      |
| 8      | 8    | int64  | ContentLength      | Byte count of the content following this header. |
| 16     | 8    | int64  | NextSegmentOffset  | Absolute byte offset to the next segment header. `0` or past-end = last segment. |
| 24     | 4    | int32  | ChunkCount         | Number of data chunks (0 for metadata segments). |
| 28     | 4    | uint32 | Crc32              | CRC-32 of segment content. `0` = not computed.   |

### Segment Types

| Value | Name     | Description                              |
|-------|----------|------------------------------------------|
| 1     | Metadata | Group and channel definitions + properties |
| 2     | Data     | Channel data chunks                       |
| 3     | Index    | Reserved for future index/seek support    |

**Linked list traversal**: To read all segments, start at `FirstSegmentOffset`, read the segment header, skip `ContentLength` bytes, then follow `NextSegmentOffset`. Stop when `NextSegmentOffset ≤ current offset` or `NextSegmentOffset ≥ file size`.

### §4a. Segment Compression

The lower 4 bits of `Flags` (bits 0–3) encode the compression algorithm applied to the segment content:

| Value | Name | Description |
|-------|------|-------------|
| 0     | None | Content is uncompressed (default). |
| 1     | LZ4  | Content is LZ4-compressed. Wire format: `[int32: originalSize][LZ4 frame]`. |
| 2     | Zstd | Content is Zstandard-compressed. Wire format: standard Zstd frame (self-describing size). |
| 3–15  | —    | Reserved for future algorithms. |

**Compression scope**: Compression applies to the segment content only — the 32-byte segment header is always uncompressed. `ContentLength` stores the **compressed** size (i.e., the exact number of bytes following the header on disk).

**LZ4 wire format**: A 4-byte little-endian `int32` prefix stores the original uncompressed size, followed by the LZ4-compressed payload. The prefix is needed because LZ4 requires the output buffer size at decompression time.

**Zstd wire format**: A standard Zstd frame which is self-describing (the decompressed size is embedded in the frame header by default).

**Reader requirement**: A conforming reader MUST check the Flags field and decompress the content before parsing chunks. A reader MAY reject unknown compression values (3–15) with an error.

**Writer requirement**: A writer MUST set the Flags field to the compression algorithm used. Metadata segments SHOULD NOT be compressed (Flags = 0) to allow fast header-only reads.

---

## 6. Metadata Segment

The metadata segment content encodes all groups, channels, and properties:

```
MetadataContent :=
  [int32: groupCount]
  Group[groupCount]

Group :=
  [String: name]
  [int32: propertyCount]
  Property[propertyCount]
  [int32: channelCount]
  Channel[channelCount]

Channel :=
  [String: name]
  [byte: dataType]          // MeasDataType enum value
  [int32: propertyCount]
  Property[propertyCount]
```

**Channel ordering**: Channels are assigned a zero-based **global index** in the order they appear: all channels of group 0, then all channels of group 1, etc. Data chunks reference channels by this global index.

---

## 7. Data Segment

A data segment contains one or more data chunks, each belonging to a specific channel:

```
DataContent :=
  [int32: chunkCount]
  Chunk[chunkCount]

Chunk :=
  [int32: channelIndex]     // Global channel index (from metadata)
  [int64: sampleCount]      // Number of samples in this chunk
  [int64: dataByteLength]   // Total bytes of raw data following
  [bytes: raw data]         // Exactly dataByteLength bytes
```

### Fixed-size channel data

For channels with fixed-size data types (int8..uint64, float32, float64, timestamp, bool):

```
raw data = sampleCount × sizeof(dataType) bytes
```

Values are stored sequentially in little-endian format. Zero-copy memory-mapping is possible on little-endian platforms.

### Variable-size channel data (Binary and Utf8String types)

For channels with `dataType = Binary (0x31)` or `dataType = Utf8String (0x30)`, each sample is a length-prefixed frame:

```
raw data :=
  Frame[sampleCount]

Frame :=
  [int32: frameByteLength]
  [bytes: frame data]       // Exactly frameByteLength bytes
```

For `Utf8String`, `frame data` is the UTF-8 encoded string without a null terminator. `frameByteLength` is the byte count, not the character count.

---

## 8. Data Types

| Value  | Name      | CLR Type      | Size (bytes) | Description                    |
|--------|-----------|---------------|--------------|--------------------------------|
| `0x01` | Int8      | sbyte         | 1            | Signed 8-bit integer           |
| `0x02` | Int16     | short         | 2            | Signed 16-bit integer (LE)     |
| `0x03` | Int32     | int           | 4            | Signed 32-bit integer (LE)     |
| `0x04` | Int64     | long          | 8            | Signed 64-bit integer (LE)     |
| `0x05` | UInt8     | byte          | 1            | Unsigned 8-bit integer         |
| `0x06` | UInt16    | ushort        | 2            | Unsigned 16-bit integer (LE)   |
| `0x07` | UInt32    | uint          | 4            | Unsigned 32-bit integer (LE)   |
| `0x08` | UInt64    | ulong         | 8            | Unsigned 64-bit integer (LE)   |
| `0x10` | Float32   | float         | 4            | IEEE 754 single precision (LE) |
| `0x11` | Float64   | double        | 8            | IEEE 754 double precision (LE) |
| `0x20` | Timestamp | int64         | 8            | Nanoseconds since Unix epoch   |
| `0x21` | TimeSpan  | int64         | 8            | Duration in nanoseconds        |
| `0x30` | Utf8String| string        | variable     | Length-prefixed UTF-8           |
| `0x31` | Binary    | byte[]        | variable     | Length-prefixed byte array      |
| `0x50` | Bool      | byte          | 1            | `0x00` = false, non-zero = true |

---

## 9. Property System

Properties are key-value pairs attached to groups and channels.

```
Property :=
  [String: key]             // UTF-8 property name
  [byte: valueType]         // MeasDataType of the value
  [value: type-dependent]   // See below
```

### Property value encoding by type

| Type       | Encoding                                   |
|------------|--------------------------------------------|
| Int32      | `[int32: value]`                           |
| Int64      | `[int64: value]`                           |
| Timestamp  | `[int64: nanoseconds]`                     |
| Float32    | `[float32: value]`                         |
| Float64    | `[float64: value]`                         |
| Utf8String | `[String: value]`                          |
| Bool       | `[byte: 0x00 or 0x01]`                     |
| Binary     | `[int32: byteLength] [bytes: data]`        |

### Reserved property keys

| Key                    | Type      | Description                              |
|------------------------|-----------|------------------------------------------|
| `MEAS.bus_def`          | Binary    | Serialized bus channel definition (§10)  |
| `MEAS.source_channel`   | Utf8String| Name of the raw source channel           |
| `MEAS.start_bit`        | Int32     | Signal start bit position                |
| `MEAS.bit_length`       | Int32     | Signal bit length                        |
| `MEAS.factor`           | Float64   | Signal scaling factor                    |
| `MEAS.offset`           | Float64   | Signal scaling offset                    |
| `MEAS.stats.count`      | Int64     | Sample count (statistics)                |
| `MEAS.stats.min`        | Float64   | Minimum value                            |
| `MEAS.stats.max`        | Float64   | Maximum value                            |
| `MEAS.stats.sum`        | Float64   | Sum of all values                        |
| `MEAS.stats.mean`       | Float64   | Arithmetic mean                          |
| `MEAS.stats.variance`   | Float64   | Population variance                      |
| `MEAS.stats.stddev`     | Float64   | Population standard deviation            |
| `MEAS.stats.first`      | Float64   | First sample value                       |
| `MEAS.stats.last`       | Float64   | Last sample value                        |

---

## 10. Bus Metadata (AUTOSAR)

When a group represents a bus channel (CAN, LIN, FlexRay, etc.), the complete bus definition is stored as a binary blob in the group property `MEAS.bus_def`.

### Bus Metadata Format

```
BusMetadata :=
  [byte: formatVersion]        // Currently 1
  [BusConfig]                  // Bus-type-specific config
  [String: rawFrameChannelName]
  [String: timestampChannelName]
  [int32: frameCount]
  FrameDefinition[frameCount]
  [int32: valueTableCount]
  ValueTable[valueTableCount]
```

### 10.1 BusConfig

```
BusConfig := [byte: busType] [bus-specific fields...]
```

| BusType | Value | Additional Fields                                                    |
|---------|-------|----------------------------------------------------------------------|
| None    | 0     | (none)                                                               |
| CAN     | 1     | `[bool: isExtendedId] [int32: baudRate]`                             |
| CAN-FD  | 2     | `[bool: isExtendedId] [int32: arbBaudRate] [int32: dataBaudRate]`    |
| LIN     | 3     | `[int32: baudRate] [byte: linVersion]`                               |
| FlexRay | 4     | `[int32: cycleTimeUs] [int32: macroticksPerCycle]`                   |
| Ethernet| 5     | (none)                                                               |
| MOST    | 6     | (none)                                                               |

### 10.2 FrameDefinition

Frames are polymorphic — the bus type determines which extra fields follow the common header.

```
FrameDefinition :=
  [String: name]
  [uint32: frameId]
  [int32: payloadLength]
  [byte: direction]            // 0=Rx, 1=Tx, 2=TxRq
  [uint16: flags]              // Bit flags (Error=1, Remote=2, WakeUp=4, SingleShot=8)
  [bus-specific fields...]     // See below
  [int32: signalCount]
  SignalDefinition[signalCount]
  [int32: pduCount]
  PduDefinition[pduCount]
```

**Bus-specific frame fields:**

| Bus Type | Fields                                                                     |
|----------|----------------------------------------------------------------------------|
| CAN      | `[bool: isExtendedId]`                                                     |
| CAN-FD   | `[bool: isExtendedId] [bool: bitRateSwitch] [bool: errorStateIndicator]`   |
| LIN      | `[byte: nad] [byte: checksumType]`                                         |
| FlexRay  | `[byte: cycleCount] [byte: channel]`                                       |
| Ethernet | `[6B: macSource] [6B: macDest] [uint16: vlanId] [uint16: etherType]`       |
| MOST     | `[uint16: functionBlock] [byte: instanceId] [uint16: functionId]`          |

### 10.3 SignalDefinition

```
SignalDefinition :=
  [String: name]
  [int32: startBit]
  [int32: bitLength]
  [byte: byteOrder]            // 0=Intel/LE, 1=Motorola/BE
  [byte: signalDataType]       // 0=Unsigned, 1=Signed, 2=Float32, 3=Float64
  [float64: factor]
  [float64: offset]
  [byte: minMaxFlags]          // Bit 0: hasMin, Bit 1: hasMax
  [float64: minValue]?         // Present only if bit 0 set
  [float64: maxValue]?         // Present only if bit 1 set
  [bool: hasUnit]
  [String: unit]?              // Present only if hasUnit
  [bool: isMultiplexer]
  [bool: hasMultiplexCondition]
  [MultiplexCondition]?        // Present only if hasMultiplexCondition
  [int32: valueDescCount]
  ValueDescription[valueDescCount]
```

**Physical value calculation**: `physical = raw × factor + offset`

**Byte order / CAN DBC bit numbering**:
- Intel (Little-Endian, value `0`): Standard LSB bit numbering
- Motorola (Big-Endian, value `1`): CAN DBC Motorola bit numbering (MSB in start byte)

### 10.4 MultiplexCondition (recursive)

```
MultiplexCondition :=
  [String: multiplexerSignalName]
  [int64: lowValue]            // Inclusive lower bound
  [int64: highValue]           // Inclusive upper bound
  [bool: hasParent]
  [MultiplexCondition]?        // Recursive nested MUX
```

A signal is active when the multiplexer signal's raw value is in `[lowValue, highValue]` AND the parent condition (if any) is also satisfied.

### 10.5 PduDefinition

```
PduDefinition :=
  [String: name]
  [uint32: pduId]
  [int32: byteOffset]          // Offset within the frame payload
  [int32: length]              // PDU length in bytes
  [bool: isContainerPdu]
  [bool: hasE2E]
  [E2EProtection]?
  [bool: hasSecOc]
  [SecOcConfig]?
  [bool: hasMultiplexing]
  [MultiplexConfig]?
  [int32: signalCount]
  SignalDefinition[signalCount]
  [int32: containedPduCount]
  ContainedPdu[containedPduCount]
```

### 10.6 E2E Protection (AUTOSAR End-to-End)

```
E2EProtection :=
  [byte: profile]              // 1=Profile01, 2=Profile02, ..., 11=Profile11, 0xFF=JLR
  [int32: crcStartBit]
  [int32: crcBitLength]
  [int32: counterStartBit]
  [int32: counterBitLength]
  [uint32: dataId]
  [uint32: crcPolynomial]
```

### 10.7 SecOC (Secure Onboard Communication)

```
SecOcConfig :=
  [byte: algorithm]            // 0=CmacAes128, 1=CmacAes256, 2=HmacSha256, 3=HmacSha384
  [int32: freshnessValueStartBit]
  [int32: freshnessValueTruncatedLength]
  [int32: freshnessValueFullLength]
  [byte: freshnessType]        // 0=Counter, 1=Timestamp, 2=Both
  [int32: macStartBit]
  [int32: macTruncatedLength]
  [int32: macFullLength]
  [int32: authenticPayloadLength]
  [uint32: dataId]
  [int32: authBuildAttempts]
  [bool: useFreshnessValueManager]
  [uint32: keyId]
```

### 10.8 MultiplexConfig

```
MultiplexConfig :=
  [String: multiplexerSignalName]
  [int32: groupCount]
  MuxGroup[groupCount]

MuxGroup :=
  [int64: muxValue]
  [int32: signalNameCount]
  String[signalNameCount]
```

### 10.9 ContainedPdu (AUTOSAR I-PDU Multiplexing)

```
ContainedPdu :=
  [String: name]
  [uint32: headerId]
  [int32: length]
  [int32: signalCount]
  SignalDefinition[signalCount]
```

### 10.10 ValueTable

```
ValueTable :=
  [String: name]
  [int32: entryCount]
  ValueTableEntry[entryCount]

ValueTableEntry :=
  [int64: value]
  [String: description]
```

---

## 11. Raw Frame Wire Formats

When bus data is recorded, raw frames are stored in the Binary channel. Each bus type uses a standardized wire format:

### CAN / CAN-FD

```
[uint32: arbitrationId]    // 11-bit or 29-bit (extended) ID
[byte: dlc]                // Data Length Code
[byte: flags]              // Bit 0: BRS, Bit 1: ESI, Bit 2: ExtendedId
[bytes: payload]           // dlc bytes of CAN data
```

Total: `6 + dlc` bytes per frame

### LIN

```
[byte: frameId]            // 6-bit LIN frame identifier (0-63)
[byte: dlc]                // Payload length
[byte: nad]                // Node Address for Diagnostic
[byte: checksumType]       // 0=Classic, 1=Enhanced
[bytes: payload]           // dlc bytes
```

Total: `4 + dlc` bytes per frame

### FlexRay

```
[uint16: slotId]           // Slot ID
[byte: cycleCount]         // Cycle counter
[byte: channelFlags]       // Bit 0: ChA, Bit 1: ChB
[uint16: payloadLength]    // Payload length in bytes
[bytes: payload]           // payloadLength bytes
```

Total: `6 + payloadLength` bytes per frame

### Ethernet

```
[6 bytes: macDestination]
[6 bytes: macSource]
[uint16: etherType]        // e.g. 0x0800 = IPv4
[uint16: vlanId]           // 0 = no VLAN
[uint16: payloadLength]
[bytes: payload]           // payloadLength bytes
```

Total: `18 + payloadLength` bytes per frame

---

## 12. Streaming Model

The MEAS format is designed for **true streaming** — data can be written and consumed incrementally.

### 12.1 Write Streaming

A conforming writer MUST support incremental flushing:

1. **Open**: Write the 64-byte file header with `SegmentCount = 0`.
2. **Define structure**: Build group/channel definitions in memory.
3. **First flush**: Write the Metadata segment, then the first Data segment.
4. **Subsequent flushes**: Each `Flush()` writes a new Data segment with only the data buffered since the last flush.
5. **Close**: Write any remaining buffered data as a final Data segment. Then seek back to patch two locations:
   - The file header at offset 0: write the final `SegmentCount`.
   - The Metadata segment content: overwrite the `MEAS.stats.*` channel properties with the final accumulated statistics (see §12.5).

**Memory invariant**: After each flush, the writer's internal buffers are cleared. A writer streaming 10 GB of data needs only O(chunk_size) memory at any time.

### 12.2 Read Streaming

A conforming reader supports two modes:

**Full read**: Walk all segments from `FirstSegmentOffset`, accumulate all data chunks per channel.

**Chunk-based read**: Return data chunk-by-chunk (one per flush/segment), enabling the consumer to process data incrementally without loading the entire file:

```
foreach chunk in channel.ReadChunks():
    process(chunk)   // Only one chunk in memory at a time
```

### 12.3 Segment Chain Integrity

Each segment's `NextSegmentOffset` points to the next segment header. This creates a forward-linked list that:

- Enables sequential scanning without an index
- Allows partial file reads (stop after N segments)
- Supports recovery from truncated files (stop when `NextSegmentOffset` is invalid)

### 12.4 Concurrent Write/Read

The segment-based design allows concurrent access:

- **Writer** flushes segments atomically (header + content)
- **Reader** can open the file with `FileShare.Read` and walk segments that have been completed
- A reader seeing `SegmentCount = 0` can walk until `NextSegmentOffset` points beyond its known file size

### 12.5 Statistics During Streaming

Channel statistics (min, max, mean, variance, count) are computed incrementally using **Welford's online algorithm** during writes. This means:

- Statistics are updated in memory after every sample write — no buffering of raw data needed.
- On `Close()`, the writer **seeks back** to the Metadata segment and overwrites the `MEAS.stats.*` channel properties in-place with the final accumulated values.
- A reader opened after `Close()` can access statistics **without reading any data chunks**.

**Patch-on-close design**: The Metadata segment is written first (with zeroed/absent statistics) and then patched at the end. This requires the backing storage to be **seekable** (a regular file). Pure pipe or non-seekable stream writers MUST omit statistics properties entirely rather than writing incorrect values.

---

## 13. Channel Statistics

Numeric channels (Int*, UInt*, Float*) automatically compute statistics using Welford's online algorithm:

```
For each new sample x:
  count++
  delta = x - mean
  mean += delta / count
  m2 += delta * (x - mean)
  variance = m2 / count     // Population variance

  min = min(min, x)
  max = max(max, x)
  sum += x
  first = (count == 1) ? x : first
  last = x
```

Statistics are stored as channel properties with `MEAS.stats.*` keys (see §9).

---

## 14. Conformance Requirements

### Writer conformance

A conforming writer MUST:
- Write a valid 64-byte file header with `Magic = 0x5341454D` and `Version = 1`
- Write at least one Metadata segment before any Data segments
- Use little-endian encoding for all multi-byte values
- Store valid `NextSegmentOffset` in every segment header
- Update `SegmentCount` in the file header on close
- Seek back and patch the Metadata segment with final `MEAS.stats.*` channel properties on close (patch-on-close; see §12.5)

A conforming writer SHOULD:
- Support incremental flushing (streaming writes)
- Compute and store channel statistics for numeric channels
- Use 64 KB buffer size for file I/O
- Omit `MEAS.stats.*` properties entirely when writing to a non-seekable stream

### Reader conformance

A conforming reader MUST:
- Validate the magic number and reject unknown versions
- Handle multiple Data segments (chunks are additive)
- Correctly decode both fixed-size and variable-length data

A conforming reader SHOULD:
- Support chunk-by-chunk reading for memory-efficient processing
- Handle truncated files gracefully (stop at invalid `NextSegmentOffset`)
- Expose pre-computed statistics without requiring data reads

---

## Appendix A: Hex Dump of a Minimal File

A minimal valid .meas file with one group, one Float32 channel, and one sample (value `42.0`):

```
Offset  Hex                                              ASCII
00000   4D 45 41 53 01 00 00 00  40 00 00 00 00 00 00 00  MEAS.....@.......
00010   00 00 00 00 00 00 00 00  02 00 00 00 00 00 00 00  ................
00020   xx xx xx xx xx xx xx xx  xx xx xx xx xx xx xx xx  [GUID - 16 B]
00030   xx xx xx xx xx xx xx xx  00 00 00 00 00 00 00 00  [timestamp][pad]

        --- Metadata Segment Header (32 B) ---
00040   01 00 00 00 00 00 00 00  xx xx xx xx xx xx xx xx  [Type=1][Flags=0]
00050   xx xx xx xx xx xx xx xx  00 00 00 00 00 00 00 00  [NextOff][0 chunks]

        --- Metadata Content ---
0005C   01 00 00 00              -- groupCount = 1
00060   04 00 00 00 54 65 73 74  -- String "Test" (4 bytes)
00068   00 00 00 00              -- 0 properties
0006C   01 00 00 00              -- channelCount = 1
00070   05 00 00 00 56 61 6C 75  -- String "Value" (5 bytes)
00078   65 10                    -- 'e' + dataType = 0x10 (Float32)
0007A   00 00 00 00              -- 0 channel properties

        --- Data Segment Header (32 B) ---
        02 00 00 00 00 00 00 00  xx xx xx xx xx xx xx xx  [Type=2][Flags=0]
        xx xx xx xx xx xx xx xx  01 00 00 00 00 00 00 00  [NextOff][1 chunk]

        --- Data Content ---
        01 00 00 00              -- chunkCount = 1
        00 00 00 00              -- channelIndex = 0
        01 00 00 00 00 00 00 00  -- sampleCount = 1
        04 00 00 00 00 00 00 00  -- dataByteLength = 4
        00 00 28 42              -- float32 42.0 (LE)
```

---

## Appendix B: Version History

| Version | Date       | Changes                         |
|---------|------------|---------------------------------|
| 1.0     | 2026-03    | Initial specification           |

---

*This specification is released under the MIT License as part of the MeasFlow project.*
