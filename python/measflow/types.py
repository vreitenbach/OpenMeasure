"""Core data types for the MeasFlow format."""

from __future__ import annotations

import struct
from datetime import datetime, timezone
from enum import IntEnum
from typing import Any


class MeasDataType(IntEnum):
    """Data type codes as stored in the binary format."""

    Int8 = 0x01
    Int16 = 0x02
    Int32 = 0x03
    Int64 = 0x04
    UInt8 = 0x05
    UInt16 = 0x06
    UInt32 = 0x07
    UInt64 = 0x08
    Float32 = 0x10
    Float64 = 0x11
    Timestamp = 0x20
    TimeSpan = 0x21
    Utf8String = 0x30
    Binary = 0x31
    Bool = 0x50


# Size in bytes for fixed-size types (None for variable-size)
_TYPE_SIZES: dict[MeasDataType, int | None] = {
    MeasDataType.Int8: 1,
    MeasDataType.Int16: 2,
    MeasDataType.Int32: 4,
    MeasDataType.Int64: 8,
    MeasDataType.UInt8: 1,
    MeasDataType.UInt16: 2,
    MeasDataType.UInt32: 4,
    MeasDataType.UInt64: 8,
    MeasDataType.Float32: 4,
    MeasDataType.Float64: 8,
    MeasDataType.Timestamp: 8,
    MeasDataType.TimeSpan: 8,
    MeasDataType.Utf8String: None,
    MeasDataType.Binary: None,
    MeasDataType.Bool: 1,
}

# struct format chars for fixed-size types (little-endian)
_TYPE_STRUCT: dict[MeasDataType, str] = {
    MeasDataType.Int8: "b",
    MeasDataType.Int16: "<h",
    MeasDataType.Int32: "<i",
    MeasDataType.Int64: "<q",
    MeasDataType.UInt8: "B",
    MeasDataType.UInt16: "<H",
    MeasDataType.UInt32: "<I",
    MeasDataType.UInt64: "<Q",
    MeasDataType.Float32: "<f",
    MeasDataType.Float64: "<d",
    MeasDataType.Timestamp: "<q",
    MeasDataType.TimeSpan: "<q",
    MeasDataType.Bool: "B",
}

# numpy dtype strings for fixed-size types
_TYPE_NUMPY: dict[MeasDataType, str] = {
    MeasDataType.Int8: "<i1",
    MeasDataType.Int16: "<i2",
    MeasDataType.Int32: "<i4",
    MeasDataType.Int64: "<i8",
    MeasDataType.UInt8: "<u1",
    MeasDataType.UInt16: "<u2",
    MeasDataType.UInt32: "<u4",
    MeasDataType.UInt64: "<u8",
    MeasDataType.Float32: "<f4",
    MeasDataType.Float64: "<f8",
    MeasDataType.Timestamp: "<i8",
    MeasDataType.TimeSpan: "<i8",
    MeasDataType.Bool: "<u1",
}


def type_size(dt: MeasDataType) -> int | None:
    """Return byte size for fixed-size types, None for variable-size."""
    return _TYPE_SIZES[dt]


def is_numeric(dt: MeasDataType) -> bool:
    """Return True if the type supports statistics (numeric or timestamp)."""
    return dt in (
        MeasDataType.Int8, MeasDataType.Int16, MeasDataType.Int32, MeasDataType.Int64,
        MeasDataType.UInt8, MeasDataType.UInt16, MeasDataType.UInt32, MeasDataType.UInt64,
        MeasDataType.Float32, MeasDataType.Float64,
    )


class MeasTimestamp:
    """Nanosecond-precision UTC timestamp (nanoseconds since Unix epoch)."""

    __slots__ = ("nanoseconds",)

    def __init__(self, nanoseconds: int = 0):
        self.nanoseconds = nanoseconds

    @classmethod
    def now(cls) -> MeasTimestamp:
        dt = datetime.now(timezone.utc)
        ns = int(dt.timestamp() * 1_000_000_000)
        return cls(ns)

    @classmethod
    def from_datetime(cls, dt: datetime) -> MeasTimestamp:
        ns = int(dt.timestamp() * 1_000_000_000)
        return cls(ns)

    def to_datetime(self) -> datetime:
        return datetime.fromtimestamp(self.nanoseconds / 1_000_000_000, tz=timezone.utc)

    def __repr__(self) -> str:
        return f"MeasTimestamp({self.to_datetime().isoformat()})"

    def __eq__(self, other: object) -> bool:
        return isinstance(other, MeasTimestamp) and self.nanoseconds == other.nanoseconds

    def __add__(self, other: Any) -> MeasTimestamp:
        if hasattr(other, "total_seconds"):
            ns = int(other.total_seconds() * 1_000_000_000)
            return MeasTimestamp(self.nanoseconds + ns)
        raise TypeError(f"unsupported operand type for +: 'MeasTimestamp' and '{type(other).__name__}'")


class MeasValue:
    """A typed property value."""

    __slots__ = ("data_type", "value")

    def __init__(self, data_type: MeasDataType, value: Any):
        self.data_type = data_type
        self.value = value

    @classmethod
    def from_python(cls, value: Any) -> MeasValue:
        """Infer MeasValue from a Python value."""
        if isinstance(value, bool):
            return cls(MeasDataType.Bool, value)
        if isinstance(value, int):
            return cls(MeasDataType.Int64, value)
        if isinstance(value, float):
            return cls(MeasDataType.Float64, value)
        if isinstance(value, str):
            return cls(MeasDataType.Utf8String, value)
        if isinstance(value, bytes):
            return cls(MeasDataType.Binary, value)
        if isinstance(value, MeasTimestamp):
            return cls(MeasDataType.Timestamp, value.nanoseconds)
        raise TypeError(f"Cannot convert {type(value).__name__} to MeasValue")

    def __repr__(self) -> str:
        return f"MeasValue({self.data_type.name}, {self.value!r})"
