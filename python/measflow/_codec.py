"""Low-level binary encoding/decoding for the MEAS format."""

from __future__ import annotations

import struct
import uuid
from dataclasses import dataclass, field
from typing import BinaryIO

from measflow.types import MeasDataType, MeasValue, _TYPE_STRUCT

# ── Constants ──────────────────────────────────────────────────────────────

MAGIC = 0x5341454D  # "MEAS" in little-endian (0x4D 0x45 0x41 0x53)
VERSION = 1
FILE_HEADER_SIZE = 64
SEGMENT_HEADER_SIZE = 32


class SegmentType:
    METADATA = 1
    DATA = 2
    INDEX = 3


# ── File Header ────────────────────────────────────────────────────────────

@dataclass
class FileHeader:
    magic: int = MAGIC
    version: int = VERSION
    flags: int = 0
    first_segment_offset: int = FILE_HEADER_SIZE
    index_offset: int = 0
    segment_count: int = 0
    file_id: bytes = field(default_factory=lambda: uuid.uuid4().bytes)
    created_at_nanos: int = 0
    reserved: int = 0

    _FMT = "<IHHqqq16sqq"

    def to_bytes(self) -> bytes:
        return struct.pack(
            self._FMT,
            self.magic, self.version, self.flags,
            self.first_segment_offset, self.index_offset, self.segment_count,
            self.file_id,
            self.created_at_nanos, self.reserved,
        )

    @classmethod
    def from_bytes(cls, data: bytes) -> FileHeader:
        (magic, version, flags, first_seg, index_off, seg_count,
         file_id, created_at, reserved) = struct.unpack(cls._FMT, data[:FILE_HEADER_SIZE])
        if magic != MAGIC:
            raise ValueError(f"Not a .meas file (magic=0x{magic:08X}, expected 0x{MAGIC:08X})")
        if version != VERSION:
            raise ValueError(f"Unsupported .meas version {version}")
        return cls(magic, version, flags, first_seg, index_off, seg_count,
                   file_id, created_at, reserved)


# ── Segment Header ──────────────────────────────────────────────────────────

@dataclass
class SegmentHeader:
    type: int = 0
    flags: int = 0
    content_length: int = 0
    next_segment_offset: int = 0
    chunk_count: int = 0
    crc32: int = 0

    _FMT = "<iiqqi I"  # Note: space before I is intentional for readability

    @staticmethod
    def _pack_fmt() -> str:
        return "<iiqqi I"

    def to_bytes(self) -> bytes:
        return struct.pack(
            "<iiqqi I",
            self.type, self.flags, self.content_length,
            self.next_segment_offset, self.chunk_count, self.crc32,
        )

    @classmethod
    def from_bytes(cls, data: bytes) -> SegmentHeader:
        (stype, flags, content_len, next_off, chunk_count, crc) = struct.unpack(
            "<iiqqi I", data[:SEGMENT_HEADER_SIZE]
        )
        return cls(stype, flags, content_len, next_off, chunk_count, crc)


# ── String Encoding ─────────────────────────────────────────────────────────

def write_string(f: BinaryIO, s: str) -> int:
    """Write a length-prefixed UTF-8 string. Returns bytes written."""
    encoded = s.encode("utf-8")
    f.write(struct.pack("<i", len(encoded)))
    f.write(encoded)
    return 4 + len(encoded)


def read_string(f: BinaryIO) -> str:
    """Read a length-prefixed UTF-8 string."""
    (length,) = struct.unpack("<i", f.read(4))
    return f.read(length).decode("utf-8")


def write_string_bytes(s: str) -> bytes:
    """Encode a length-prefixed UTF-8 string to bytes."""
    encoded = s.encode("utf-8")
    return struct.pack("<i", len(encoded)) + encoded


def read_string_from(data: bytes, offset: int) -> tuple[str, int]:
    """Read string from buffer at offset. Returns (string, new_offset)."""
    (length,) = struct.unpack_from("<i", data, offset)
    s = data[offset + 4: offset + 4 + length].decode("utf-8")
    return s, offset + 4 + length


# ── Property Encoding ───────────────────────────────────────────────────────

def encode_property_value(val: MeasValue) -> bytes:
    """Encode a single property value (type byte + value bytes)."""
    parts = [struct.pack("B", val.data_type)]
    dt = val.data_type
    v = val.value

    if dt in _TYPE_STRUCT and dt not in (MeasDataType.Utf8String, MeasDataType.Binary):
        parts.append(struct.pack(_TYPE_STRUCT[dt], v))
    elif dt == MeasDataType.Utf8String:
        parts.append(write_string_bytes(v))
    elif dt == MeasDataType.Binary:
        parts.append(struct.pack("<i", len(v)))
        parts.append(v)
    else:
        raise ValueError(f"Cannot encode property of type {dt}")

    return b"".join(parts)


def decode_property_value(data: bytes, offset: int) -> tuple[MeasValue, int]:
    """Decode a property value at offset. Returns (MeasValue, new_offset)."""
    dt = MeasDataType(data[offset])
    offset += 1

    if dt == MeasDataType.Utf8String:
        s, offset = read_string_from(data, offset)
        return MeasValue(dt, s), offset
    elif dt == MeasDataType.Binary:
        (length,) = struct.unpack_from("<i", data, offset)
        offset += 4
        return MeasValue(dt, data[offset: offset + length]), offset + length
    elif dt in _TYPE_STRUCT:
        fmt = _TYPE_STRUCT[dt]
        size = struct.calcsize(fmt)
        (v,) = struct.unpack_from(fmt, data, offset)
        if dt == MeasDataType.Bool:
            v = bool(v)
        return MeasValue(dt, v), offset + size
    else:
        raise ValueError(f"Unknown property type {dt}")


def encode_properties(props: dict[str, MeasValue]) -> bytes:
    """Encode a property dict: [int32 count] [key-value pairs]."""
    parts = [struct.pack("<i", len(props))]
    for key, val in props.items():
        parts.append(write_string_bytes(key))
        parts.append(encode_property_value(val))
    return b"".join(parts)


def decode_properties(data: bytes, offset: int) -> tuple[dict[str, MeasValue], int]:
    """Decode properties from buffer. Returns (dict, new_offset)."""
    (count,) = struct.unpack_from("<i", data, offset)
    offset += 4
    props: dict[str, MeasValue] = {}
    for _ in range(count):
        key, offset = read_string_from(data, offset)
        val, offset = decode_property_value(data, offset)
        props[key] = val
    return props, offset


# ── Metadata Encoding ───────────────────────────────────────────────────────

@dataclass
class ChannelDef:
    name: str
    data_type: MeasDataType
    properties: dict[str, MeasValue] = field(default_factory=dict)


@dataclass
class GroupDef:
    name: str
    properties: dict[str, MeasValue] = field(default_factory=dict)
    channels: list[ChannelDef] = field(default_factory=list)


FLAG_EXTENDED_METADATA = 0x0001

# Current metadata format version (§6)
META_MAJOR = 0
META_MINOR = 1


def encode_metadata(groups: list[GroupDef],
                    file_properties: dict[str, MeasValue] | None = None,
                    extended: bool = False) -> bytes:
    """Encode group/channel definitions to metadata bytes.

    When *extended* is True (or *file_properties* is non-empty), the output
    starts with [metaMajor][metaMinor][fileProps...] (the caller must also set
    Flags bit 0 in the file header).
    """
    extended = extended or bool(file_properties)
    parts: list[bytes] = []
    if extended:
        parts.append(struct.pack("BB", META_MAJOR, META_MINOR))
        parts.append(encode_properties(file_properties or {}))
    parts.append(struct.pack("<i", len(groups)))
    for g in groups:
        parts.append(write_string_bytes(g.name))
        parts.append(encode_properties(g.properties))
        parts.append(struct.pack("<i", len(g.channels)))
        for ch in g.channels:
            parts.append(write_string_bytes(ch.name))
            parts.append(struct.pack("B", ch.data_type))
            parts.append(encode_properties(ch.properties))
    return b"".join(parts)


def decode_metadata(data: bytes,
                    extended_metadata: bool = False,
                    file_properties_out: dict[str, MeasValue] | None = None,
                    ) -> list[GroupDef]:
    """Decode metadata bytes into group definitions.

    When *extended_metadata* is True, the data starts with a 2-byte version
    prefix followed by file-level properties before the group count.
    """
    offset = 0
    if extended_metadata:
        major = data[offset]; offset += 1
        minor = data[offset]; offset += 1
        if major > META_MAJOR:
            raise ValueError(
                f"Unsupported metadata version {major}.{minor} "
                f"(max supported: {META_MAJOR}.{META_MINOR})")
        if major == META_MAJOR and minor > META_MINOR:
            raise ValueError(
                f"Unsupported metadata minor version {major}.{minor} "
                f"(max supported: {META_MAJOR}.{META_MINOR})")
        props, offset = decode_properties(data, offset)
        if file_properties_out is not None:
            file_properties_out.update(props)
    (group_count,) = struct.unpack_from("<i", data, offset)
    offset += 4

    groups = []
    for _ in range(group_count):
        name, offset = read_string_from(data, offset)
        props, offset = decode_properties(data, offset)
        (ch_count,) = struct.unpack_from("<i", data, offset)
        offset += 4

        channels = []
        for _ in range(ch_count):
            ch_name, offset = read_string_from(data, offset)
            dt = MeasDataType(data[offset])
            offset += 1
            ch_props, offset = decode_properties(data, offset)
            channels.append(ChannelDef(ch_name, dt, ch_props))

        groups.append(GroupDef(name, props, channels))

    return groups


# ── Data Chunk Header ───────────────────────────────────────────────────────

CHUNK_HEADER_FMT = "<iqq"
CHUNK_HEADER_SIZE = struct.calcsize(CHUNK_HEADER_FMT)  # 4+8+8 = 20


def encode_chunk_header(channel_index: int, sample_count: int, data_byte_length: int) -> bytes:
    return struct.pack(CHUNK_HEADER_FMT, channel_index, sample_count, data_byte_length)


def decode_chunk_header(data: bytes, offset: int) -> tuple[int, int, int, int]:
    """Returns (channel_index, sample_count, data_byte_length, new_offset)."""
    ch_idx, sample_count, data_len = struct.unpack_from(CHUNK_HEADER_FMT, data, offset)
    return ch_idx, sample_count, data_len, offset + CHUNK_HEADER_SIZE
