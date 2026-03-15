"""Writer for the .meas binary format."""

from __future__ import annotations

import math
import struct
import time
import uuid
from typing import Any, Union

import numpy as np

from measflow.types import MeasDataType, MeasTimestamp, MeasValue, _TYPE_NUMPY, is_numeric
from measflow._codec import (
    FileHeader,
    SegmentHeader,
    SegmentType,
    GroupDef,
    ChannelDef,
    encode_metadata,
    CHUNK_HEADER_FMT,
    FILE_HEADER_SIZE,
    SEGMENT_HEADER_SIZE,
)


class _StatisticsAccumulator:
    """Incremental statistics using Welford's online algorithm."""

    __slots__ = ("_count", "_min", "_max", "_sum", "_mean", "_m2", "_first", "_last")

    def __init__(self) -> None:
        self._count: int = 0
        self._min: float = 0.0
        self._max: float = 0.0
        self._sum: float = 0.0
        self._mean: float = 0.0
        self._m2: float = 0.0
        self._first: float = 0.0
        self._last: float = 0.0

    def update(self, value: float) -> None:
        self._count += 1
        self._last = value
        if self._count == 1:
            self._first = value
            self._min = value
            self._max = value
            self._sum = value
            self._mean = value
            self._m2 = 0.0
            return
        if value < self._min:
            self._min = value
        if value > self._max:
            self._max = value
        self._sum += value
        delta = value - self._mean
        self._mean += delta / self._count
        self._m2 += delta * (value - self._mean)

    def write_to_properties(self, props: dict[str, Any]) -> None:
        # Population variance: M2 / count (§13). When count <= 1, M2 == 0 so variance == 0.
        variance = self._m2 / self._count if self._count > 0 else 0.0
        props["meas.stats.count"] = MeasValue(MeasDataType.Int64, self._count)
        props["meas.stats.min"] = MeasValue(MeasDataType.Float64, self._min)
        props["meas.stats.max"] = MeasValue(MeasDataType.Float64, self._max)
        props["meas.stats.sum"] = MeasValue(MeasDataType.Float64, self._sum)
        props["meas.stats.mean"] = MeasValue(MeasDataType.Float64, self._mean)
        props["meas.stats.variance"] = MeasValue(MeasDataType.Float64, variance)
        props["meas.stats.first"] = MeasValue(MeasDataType.Float64, self._first)
        props["meas.stats.last"] = MeasValue(MeasDataType.Float64, self._last)

    def write_to_properties_placeholder(self, props: dict[str, Any]) -> None:
        """Write zeroed stats so the metadata reserves space for later patching."""
        props["meas.stats.count"] = MeasValue(MeasDataType.Int64, 0)
        props["meas.stats.min"] = MeasValue(MeasDataType.Float64, 0.0)
        props["meas.stats.max"] = MeasValue(MeasDataType.Float64, 0.0)
        props["meas.stats.sum"] = MeasValue(MeasDataType.Float64, 0.0)
        props["meas.stats.mean"] = MeasValue(MeasDataType.Float64, 0.0)
        props["meas.stats.variance"] = MeasValue(MeasDataType.Float64, 0.0)
        props["meas.stats.first"] = MeasValue(MeasDataType.Float64, 0.0)
        props["meas.stats.last"] = MeasValue(MeasDataType.Float64, 0.0)


class ChannelWriter:
    """Buffers samples for a single channel."""

    def __init__(self, name: str, dtype: MeasDataType) -> None:
        self.name = name
        self.data_type = dtype
        self.properties: dict[str, Any] = {}
        self._global_index: int = 0  # assigned when metadata is written
        self._samples: list = []
        self._stats: _StatisticsAccumulator | None = (
            _StatisticsAccumulator() if is_numeric(dtype) else None
        )

    def write(self, value: Any) -> None:
        """Append a single sample."""
        self._samples.append(value)
        if self._stats is not None:
            self._stats.update(float(value))

    def write_bulk(self, values: Any) -> None:
        """Append an array or iterable of samples."""
        if self._stats is not None:
            for v in values:
                self._samples.append(v)
                self._stats.update(float(v))
        else:
            self._samples.extend(values)

    @property
    def sample_count(self) -> int:
        return len(self._samples)

    def _to_bytes(self) -> bytes:
        if not self._samples:
            return b""
        dt = self.data_type
        if dt == MeasDataType.Timestamp:
            ns = [
                v.nanoseconds if isinstance(v, MeasTimestamp) else int(v)
                for v in self._samples
            ]
            return np.array(ns, dtype="<i8").tobytes()
        if dt in _TYPE_NUMPY:
            return np.array(self._samples, dtype=_TYPE_NUMPY[dt]).tobytes()
        if dt == MeasDataType.Binary:
            # §7: each sample is [int32: frameByteLength][bytes: data]
            parts = []
            for v in self._samples:
                b = bytes(v)
                parts.append(struct.pack("<i", len(b)))
                parts.append(b)
            return b"".join(parts)
        if dt == MeasDataType.Utf8String:
            # §7: each sample is [int32: byteLength][UTF-8 bytes]
            parts = []
            for v in self._samples:
                encoded = v.encode("utf-8") if isinstance(v, str) else bytes(v)
                parts.append(struct.pack("<i", len(encoded)))
                parts.append(encoded)
            return b"".join(parts)
        raise ValueError(f"Cannot serialize channel type {dt!r}")

    def _to_channel_def(self, with_stats: bool = False) -> ChannelDef:
        props = {
            k: (v if isinstance(v, MeasValue) else MeasValue.from_python(v))
            for k, v in self.properties.items()
        }
        if self._stats is not None:
            if with_stats:
                self._stats.write_to_properties(props)
            else:
                # Write placeholder stats (all zeros) so the metadata size is
                # reserved and can be patched in-place on close.
                _StatisticsAccumulator().write_to_properties_placeholder(props)
        return ChannelDef(self.name, self.data_type, props)


class GroupWriter:
    """Collects channels for a single group."""

    def __init__(self, name: str) -> None:
        self.name = name
        self.properties: dict[str, Any] = {}
        self._channels: list[ChannelWriter] = []

    def add_channel(
        self, name: str, dtype: MeasDataType = MeasDataType.Float64
    ) -> ChannelWriter:
        """Add a typed channel to this group."""
        ch = ChannelWriter(name, dtype)
        self._channels.append(ch)
        return ch

    def _to_group_def(self, with_stats: bool = False) -> GroupDef:
        props = {
            k: (v if isinstance(v, MeasValue) else MeasValue.from_python(v))
            for k, v in self.properties.items()
        }
        return GroupDef(self.name, props, [ch._to_channel_def(with_stats=with_stats) for ch in self._channels])


class MeasWriter:
    """Streaming writer for .meas files (§12.1).

    Supports incremental flush: each call to flush() writes a new Data segment.
    Channel statistics (§13) are computed incrementally and patched into the
    metadata segment on close so they always reflect the full dataset.
    Use as a context manager or call close() explicitly.

    Args:
        path: File path to write to.
        compression: Compression algorithm for data segments.
            ``"none"`` (default), ``"lz4"``, or ``"zstd"``.
    """

    _COMPRESSION_FLAGS = {"none": 0, "lz4": 1, "zstd": 2}

    def __init__(self, path: str, compression: str = "none") -> None:
        if compression not in self._COMPRESSION_FLAGS:
            raise ValueError(f"Unknown compression: {compression!r}. Use 'none', 'lz4', or 'zstd'.")
        self._path = path
        self._compression = compression
        self._groups: list[GroupWriter] = []
        self._segment_count = 0
        self._metadata_written = False
        self._metadata_content_offset: int = 0  # file offset of metadata content
        self._created_ns = time.time_ns()
        self._file_id = uuid.uuid4().bytes
        # Open file immediately and write placeholder header
        self._file = open(path, "wb")
        self._file.write(b"\x00" * FILE_HEADER_SIZE)

    def add_group(self, name: str) -> GroupWriter:
        """Add a measurement group. Must be called before any data is written."""
        if self._metadata_written:
            raise RuntimeError("Cannot add groups after data has been written.")
        g = GroupWriter(name)
        self._groups.append(g)
        return g

    def flush(self) -> None:
        """Flush all buffered samples to disk as a new Data segment (§12.1)."""
        self._ensure_metadata()
        pending = [
            (ch._global_index, ch)
            for g in self._groups
            for ch in g._channels
            if ch._samples
        ]
        if not pending:
            return
        self._write_data_segment(pending)
        for _, ch in pending:
            ch._samples = []
        self._file.flush()

    def close(self) -> None:
        """Flush remaining data and finalise the file header."""
        if self._file.closed:
            return
        try:
            self.flush()
            # Patch the metadata segment in-place with final statistics (§12.5).
            # Stats properties always occupy a fixed number of bytes (fixed-size
            # types), so the content length does not change.
            if self._metadata_written:
                self._patch_metadata_stats()
            # Patch SegmentCount in file header
            hdr = FileHeader(
                created_at_nanos=self._created_ns,
                segment_count=self._segment_count,
                file_id=self._file_id,
            )
            self._file.seek(0)
            self._file.write(hdr.to_bytes())
        finally:
            self._file.close()

    def __enter__(self) -> "MeasWriter":
        return self

    def __exit__(self, *args: Any) -> None:
        self.close()

    # ── Internal helpers ─────────────────────────────────────────────────────

    def _ensure_metadata(self) -> None:
        if self._metadata_written:
            return
        self._metadata_written = True
        # Assign global channel indices
        global_idx = 0
        for g in self._groups:
            for ch in g._channels:
                ch._global_index = global_idx
                global_idx += 1
        # Write actual file header (replace placeholder)
        hdr = FileHeader(
            created_at_nanos=self._created_ns,
            segment_count=0,
            file_id=self._file_id,
        )
        self._file.seek(0)
        self._file.write(hdr.to_bytes())
        self._file.seek(0, 2)  # seek to end
        # Write metadata segment with placeholder stats (will be patched on close)
        meta_content = encode_metadata([g._to_group_def(with_stats=False) for g in self._groups])
        self._metadata_content_offset = self._file.tell() + SEGMENT_HEADER_SIZE
        self._write_segment(SegmentType.METADATA, meta_content, chunk_count=0)

    def _patch_metadata_stats(self) -> None:
        """Overwrite the metadata segment content with final statistics in-place."""
        final_meta = encode_metadata([g._to_group_def(with_stats=True) for g in self._groups])
        self._file.seek(self._metadata_content_offset)
        self._file.write(final_meta)
        self._file.seek(0, 2)  # seek back to end

    def _write_segment(self, seg_type: int, content: bytes, chunk_count: int,
                        flags: int = 0) -> None:
        seg_start = self._file.tell()
        seg = SegmentHeader(
            type=seg_type,
            flags=flags,
            content_length=len(content),
            next_segment_offset=0,  # patched below
            chunk_count=chunk_count,
            crc32=0,
        )
        self._file.write(seg.to_bytes())
        self._file.write(content)
        next_off = self._file.tell()
        seg.next_segment_offset = next_off
        self._file.seek(seg_start)
        self._file.write(seg.to_bytes())
        self._file.seek(next_off)
        self._segment_count += 1

    def _compress(self, data: bytes) -> tuple[bytes, int]:
        """Compress data and return (compressed_bytes, flags)."""
        if self._compression == "lz4":
            import lz4.block
            compressed = lz4.block.compress(data, store_size=True)
            return compressed, self._COMPRESSION_FLAGS["lz4"]
        if self._compression == "zstd":
            import zstandard
            cctx = zstandard.ZstdCompressor(level=3)
            compressed = cctx.compress(data)
            return compressed, self._COMPRESSION_FLAGS["zstd"]
        return data, 0

    def _write_data_segment(self, pending: list) -> None:
        parts = [struct.pack("<i", len(pending))]
        for global_idx, ch in pending:
            raw = ch._to_bytes()
            parts.append(struct.pack(CHUNK_HEADER_FMT, global_idx, ch.sample_count, len(raw)))
            parts.append(raw)
        content = b"".join(parts)
        content, flags = self._compress(content)
        self._write_segment(SegmentType.DATA, content, chunk_count=len(pending), flags=flags)
