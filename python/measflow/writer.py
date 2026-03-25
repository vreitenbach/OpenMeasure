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
    FLAG_EXTENDED_METADATA,
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

    def update_bulk(self, arr: np.ndarray) -> None:
        """Batch-update statistics from a numpy array (much faster than per-element)."""
        if len(arr) == 0:
            return
        n = len(arr)
        arr_f64 = arr.astype(np.float64, copy=False)
        if self._count == 0:
            self._first = float(arr_f64[0])
        self._last = float(arr_f64[-1])

        arr_min = float(arr_f64.min())
        arr_max = float(arr_f64.max())
        arr_sum = float(arr_f64.sum())

        if self._count == 0:
            self._min = arr_min
            self._max = arr_max
        else:
            if arr_min < self._min:
                self._min = arr_min
            if arr_max > self._max:
                self._max = arr_max

        # Combine running mean/variance with batch using parallel algorithm
        old_count = self._count
        new_count = old_count + n
        arr_mean = arr_sum / n

        delta = arr_mean - self._mean
        new_mean = self._mean + delta * n / new_count

        # Variance of the new batch (avoid allocating temporary arrays)
        if n > 1:
            arr_m2 = float(arr_f64.var(ddof=0)) * n
        else:
            arr_m2 = 0.0

        # Combine M2 values (parallel Welford)
        self._m2 = self._m2 + arr_m2 + delta * delta * old_count * n / new_count

        self._mean = new_mean
        self._sum += arr_sum
        self._count = new_count

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


class _FrozenDict(dict):
    """A dict subclass that raises on mutation after being frozen."""

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)
        self._frozen = False

    def _check_frozen(self) -> None:
        if self._frozen:
            raise RuntimeError(
                "Cannot modify properties after metadata has been written. "
                "Set all properties before writing any data or calling flush()."
            )

    def __setitem__(self, key: Any, value: Any) -> None:
        self._check_frozen()
        super().__setitem__(key, value)

    def __delitem__(self, key: Any) -> None:
        self._check_frozen()
        super().__delitem__(key)

    def update(self, *args: Any, **kwargs: Any) -> None:
        self._check_frozen()
        super().update(*args, **kwargs)

    def pop(self, *args: Any) -> Any:
        self._check_frozen()
        return super().pop(*args)

    def clear(self) -> None:
        self._check_frozen()
        super().clear()

    def setdefault(self, key: Any, default: Any = None) -> Any:
        self._check_frozen()
        return super().setdefault(key, default)

    def popitem(self) -> tuple[Any, Any]:
        self._check_frozen()
        return super().popitem()


class ChannelWriter:
    """Buffers samples for a single channel."""

    def __init__(self, name: str, dtype: MeasDataType) -> None:
        self.name = name
        self.data_type = dtype
        self.properties: dict[str, Any] = _FrozenDict()
        self._global_index: int = 0  # assigned when metadata is written
        self._buffers: list[bytes] = []  # pre-serialized byte chunks
        self._sample_count: int = 0
        self._samples: list = []  # only used for non-numpy types (Binary, String)
        self._stats: _StatisticsAccumulator | None = (
            _StatisticsAccumulator() if is_numeric(dtype) else None
        )

    def write(self, value: Any) -> None:
        """Append a single sample."""
        self._sample_count += 1
        if self._stats is not None:
            self._stats.update(float(value))
        dt = self.data_type
        if dt in _TYPE_NUMPY:
            # Serialize immediately to _buffers to preserve ordering with write_bulk
            self._buffers.append(np.array(value, dtype=_TYPE_NUMPY[dt]).tobytes())
        elif dt == MeasDataType.Timestamp:
            ns = value.nanoseconds if isinstance(value, MeasTimestamp) else int(value)
            self._buffers.append(struct.pack("<q", ns))
        else:
            self._samples.append(value)

    def write_bulk(self, values: Any) -> None:
        """Append an array or iterable of samples.

        For numpy arrays with numeric types, the data buffer is kept as-is
        (zero-copy) to avoid unnecessary memory allocation. This is the fast path.
        """
        dt = self.data_type
        if isinstance(values, np.ndarray) and dt in _TYPE_NUMPY:
            # Fast path: keep numpy buffer directly (zero-copy)
            arr = np.ascontiguousarray(values, dtype=_TYPE_NUMPY[dt])
            self._buffers.append(arr)
            self._sample_count += len(arr)
            if self._stats is not None:
                self._stats.update_bulk(arr)
        elif isinstance(values, np.ndarray) and dt == MeasDataType.Timestamp:
            arr = np.ascontiguousarray(values, dtype="<i8")
            self._buffers.append(arr)
        else:
            # Slow path: per-element (Binary, String, or non-array input)
            if self._stats is not None:
                for v in values:
                    self._samples.append(v)
                    self._sample_count += 1
                    self._stats.update(float(v))
            else:
                for v in values:
                    self._samples.append(v)
                    self._sample_count += 1

    @property
    def sample_count(self) -> int:
        return self._sample_count

    def _data_length(self) -> int:
        """Return the total byte length of buffered data without copying."""
        n = 0
        for buf in self._buffers:
            n += buf.nbytes if isinstance(buf, np.ndarray) else len(buf)
        for v in self._samples:
            dt = self.data_type
            if dt == MeasDataType.Binary:
                n += 4 + len(bytes(v))
            elif dt == MeasDataType.Utf8String:
                n += 4 + len(v.encode("utf-8") if isinstance(v, str) else bytes(v))
            elif dt == MeasDataType.Timestamp:
                n += 8
            elif dt in _TYPE_NUMPY:
                n += np.dtype(_TYPE_NUMPY[dt]).itemsize
        return n

    def _write_data_to(self, f) -> None:
        """Write all buffered data directly to a file object (zero-copy for numpy)."""
        for buf in self._buffers:
            if isinstance(buf, np.ndarray):
                f.write(buf.data)
            else:
                f.write(buf)
        if self._samples:
            # Fall back to serialization for variable-size types
            f.write(self._serialize_samples())

    def _serialize_samples(self) -> bytes:
        """Serialize per-element samples to bytes."""
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
            frame_parts = []
            for v in self._samples:
                b = bytes(v)
                frame_parts.append(struct.pack("<i", len(b)))
                frame_parts.append(b)
            return b"".join(frame_parts)
        if dt == MeasDataType.Utf8String:
            frame_parts = []
            for v in self._samples:
                encoded = v.encode("utf-8") if isinstance(v, str) else bytes(v)
                frame_parts.append(struct.pack("<i", len(encoded)))
                frame_parts.append(encoded)
            return b"".join(frame_parts)
        raise ValueError(f"Cannot serialize channel type {dt!r}")

    def _to_bytes(self) -> bytes:
        """Serialize all buffered data to bytes (used for compression fallback)."""
        parts = [buf.tobytes() if isinstance(buf, np.ndarray) else buf
                 for buf in self._buffers]
        if self._samples:
            parts.append(self._serialize_samples())
        if not parts:
            return b""
        return b"".join(parts) if len(parts) > 1 else parts[0]

    def _clear(self) -> None:
        """Clear all buffered data after flush."""
        self._buffers.clear()
        self._samples.clear()
        self._sample_count = 0

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
        self.properties: dict[str, Any] = _FrozenDict()
        self._channels: list[ChannelWriter] = []

    def add_channel(
        self, name: str, dtype: MeasDataType = MeasDataType.Float64,
        *, track_statistics: bool = True,
    ) -> ChannelWriter:
        """Add a typed channel to this group.

        Args:
            track_statistics: If False, disable automatic min/max/mean/stddev
                computation.  Improves write performance when statistics are
                not needed (e.g. format-comparison benchmarks).
        """
        ch = ChannelWriter(name, dtype)
        if not track_statistics:
            ch._stats = None
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
        self.properties: dict[str, Any] = _FrozenDict()
        self._metadata_content_length: int = 0  # original encoded metadata size
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
            if ch._buffers or ch._samples
        ]
        if not pending:
            return
        self._write_data_segment(pending)
        for _, ch in pending:
            ch._clear()
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
                flags=getattr(self, '_header_flags', 0),
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

    @property
    def _has_file_properties(self) -> bool:
        return bool(self.properties)

    def _file_props_encoded(self) -> dict[str, MeasValue] | None:
        if not self.properties:
            return None
        return {
            k: (v if isinstance(v, MeasValue) else MeasValue.from_python(v))
            for k, v in self.properties.items()
        }

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
        # Always write extended metadata format
        self._header_flags = FLAG_EXTENDED_METADATA
        # Write actual file header (replace placeholder)
        hdr = FileHeader(
            created_at_nanos=self._created_ns,
            segment_count=0,
            file_id=self._file_id,
            flags=self._header_flags,
        )
        self._file.seek(0)
        self._file.write(hdr.to_bytes())
        self._file.seek(0, 2)  # seek to end
        # Write metadata segment with placeholder stats (will be patched on close)
        meta_content = encode_metadata(
            [g._to_group_def(with_stats=False) for g in self._groups],
            file_properties=self._file_props_encoded(),
            extended=True,
        )
        self._metadata_content_offset = self._file.tell() + SEGMENT_HEADER_SIZE
        self._metadata_content_length = len(meta_content)
        self._write_segment(SegmentType.METADATA, meta_content, chunk_count=0)
        # Freeze all property dicts so mutations cannot change metadata size
        if isinstance(self.properties, _FrozenDict):
            self.properties._frozen = True
        for g in self._groups:
            if isinstance(g.properties, _FrozenDict):
                g.properties._frozen = True
            for ch in g._channels:
                if isinstance(ch.properties, _FrozenDict):
                    ch.properties._frozen = True

    def _patch_metadata_stats(self) -> None:
        """Overwrite the metadata segment content with final statistics in-place."""
        final_meta = encode_metadata(
            [g._to_group_def(with_stats=True) for g in self._groups],
            file_properties=self._file_props_encoded(),
            extended=True,
        )
        if len(final_meta) != self._metadata_content_length:
            raise RuntimeError(
                f"Metadata size changed during repatch: original "
                f"{self._metadata_content_length} bytes, new {len(final_meta)} bytes. "
                f"This indicates properties were mutated after metadata was written."
            )
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
        if self._compression != "none":
            # Compressed: must build full content buffer for compression
            parts = [struct.pack("<i", len(pending))]
            for global_idx, ch in pending:
                raw = ch._to_bytes()
                parts.append(struct.pack(CHUNK_HEADER_FMT, global_idx, ch.sample_count, len(raw)))
                parts.append(raw)
            content = b"".join(parts)
            content, flags = self._compress(content)
            self._write_segment(SegmentType.DATA, content, chunk_count=len(pending), flags=flags)
            return

        # Uncompressed: stream data directly to file (zero-copy for numpy)
        # Calculate total content length without copying data
        content_length = 4  # chunk_count (int32)
        for global_idx, ch in pending:
            data_len = ch._data_length()
            content_length += struct.calcsize(CHUNK_HEADER_FMT) + data_len

        # Write segment header (will be patched with next_segment_offset)
        seg_start = self._file.tell()
        seg = SegmentHeader(
            type=SegmentType.DATA,
            flags=0,
            content_length=content_length,
            next_segment_offset=0,
            chunk_count=len(pending),
            crc32=0,
        )
        self._file.write(seg.to_bytes())

        # Write chunk count + per-channel headers + data directly
        self._file.write(struct.pack("<i", len(pending)))
        for global_idx, ch in pending:
            data_len = ch._data_length()
            self._file.write(struct.pack(CHUNK_HEADER_FMT, global_idx, ch.sample_count, data_len))
            ch._write_data_to(self._file)

        # Patch segment header with next_segment_offset
        next_off = self._file.tell()
        seg.next_segment_offset = next_off
        self._file.seek(seg_start)
        self._file.write(seg.to_bytes())
        self._file.seek(next_off)
        self._segment_count += 1
