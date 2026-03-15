"""Reader for the .meas binary format."""

from __future__ import annotations

import math
import struct
from dataclasses import dataclass
from typing import Any, Union

import numpy as np

from measflow.types import MeasDataType, MeasTimestamp, MeasValue, _TYPE_NUMPY
from measflow._codec import (
    FileHeader,
    SegmentHeader,
    SegmentType,
    GroupDef,
    decode_metadata,
    decode_chunk_header,
    SEGMENT_HEADER_SIZE,
)


@dataclass
class ChannelStatistics:
    """Pre-computed channel statistics stored as ``meas.stats.*`` properties."""

    count: int
    min: float
    max: float
    sum: float
    mean: float
    variance: float
    first: float
    last: float

    @property
    def std_dev(self) -> float:
        """Population standard deviation (derived from variance)."""
        return math.sqrt(max(0.0, self.variance))


class MeasChannel:
    """A single typed channel within a group."""

    def __init__(
        self,
        name: str,
        dtype: MeasDataType,
        properties: dict[str, MeasValue],
        chunks: list[tuple[int, bytes]],
    ) -> None:
        self.name = name
        self.data_type = dtype
        self.properties = properties
        self._chunks = chunks  # list of (sample_count, raw_bytes)

    @property
    def sample_count(self) -> int:
        return sum(n for n, _ in self._chunks)

    @property
    def statistics(self) -> ChannelStatistics | None:
        """Return pre-computed statistics from channel properties, or None if not available."""
        props = self.properties
        if "meas.stats.count" not in props:
            return None
        count = int(props["meas.stats.count"].value)
        if count == 0:
            return None
        return ChannelStatistics(
            count=count,
            min=float(props["meas.stats.min"].value),
            max=float(props["meas.stats.max"].value),
            sum=float(props["meas.stats.sum"].value),
            mean=float(props["meas.stats.mean"].value),
            variance=float(props["meas.stats.variance"].value),
            first=float(props["meas.stats.first"].value),
            last=float(props["meas.stats.last"].value),
        )

    def read_all(self) -> Union[np.ndarray, list]:
        """Return all samples as a numpy array (fixed-size types) or a list (variable-size types)."""
        if not self._chunks:
            if self.data_type in (MeasDataType.Binary, MeasDataType.Utf8String):
                return []
            dtype = _TYPE_NUMPY.get(self.data_type, "<i8")
            return np.array([], dtype=dtype)
        if self.data_type == MeasDataType.Binary:
            return self._decode_frames(bytes_only=True)
        if self.data_type == MeasDataType.Utf8String:
            return self._decode_frames(bytes_only=False)
        if self.data_type not in _TYPE_NUMPY:
            raise ValueError(f"Cannot decode channel type {self.data_type!r}")
        parts = [np.frombuffer(raw, dtype=_TYPE_NUMPY[self.data_type]) for _, raw in self._chunks]
        return np.concatenate(parts) if len(parts) > 1 else parts[0].copy()

    def _decode_frames(self, bytes_only: bool) -> list:
        """Decode §7 variable-size frame format: [int32: len][bytes: data] per sample."""
        import struct as _struct
        result = []
        for _, raw in self._chunks:
            result.extend(self._decode_single_chunk_frames(raw, bytes_only))
        return result

    def read_timestamps(self) -> list[MeasTimestamp]:
        """Read all samples as MeasTimestamp objects (Timestamp channels only)."""
        if self.data_type != MeasDataType.Timestamp:
            raise ValueError(f"Channel '{self.name}' has type {self.data_type.name}, not Timestamp")
        return [MeasTimestamp(int(v)) for v in self.read_all()]

    def read_chunks(self):
        """Iterate over data chunks (§12.2).

        Yields one item per Data segment written via flush():
        - ``np.ndarray`` for fixed-size types (numeric, Timestamp)
        - ``list`` for variable-size types (Binary, Utf8String)
        """
        for _, raw in self._chunks:
            if self.data_type == MeasDataType.Binary:
                yield self._decode_single_chunk_frames(raw, bytes_only=True)
            elif self.data_type == MeasDataType.Utf8String:
                yield self._decode_single_chunk_frames(raw, bytes_only=False)
            elif self.data_type in _TYPE_NUMPY:
                yield np.frombuffer(raw, dtype=_TYPE_NUMPY[self.data_type]).copy()
            else:
                raise ValueError(f"Cannot decode channel type {self.data_type!r}")

    def _decode_single_chunk_frames(self, raw: bytes, bytes_only: bool) -> list:
        import struct as _struct
        result = []
        pos = 0
        while pos < len(raw):
            (frame_len,) = _struct.unpack_from("<i", raw, pos)
            pos += 4
            frame = raw[pos: pos + frame_len]
            pos += frame_len
            result.append(frame if bytes_only else frame.decode("utf-8"))
        return result

    def __repr__(self) -> str:
        return f"MeasChannel({self.name!r}, {self.data_type.name}, samples={self.sample_count})"


class MeasGroup:
    """A named group containing one or more channels."""

    def __init__(
        self,
        name: str,
        properties: dict[str, MeasValue],
        channels: list[MeasChannel],
    ) -> None:
        self.name = name
        self.properties = properties
        self.channels = channels
        self._by_name = {ch.name: ch for ch in channels}

    def __getitem__(self, name: str) -> MeasChannel:
        if name not in self._by_name:
            raise KeyError(f"Channel '{name}' not found in group '{self.name}'")
        return self._by_name[name]

    def __repr__(self) -> str:
        return f"MeasGroup({self.name!r}, channels={[ch.name for ch in self.channels]})"


class MeasReader:
    """Read a .meas file. Use as a context manager or construct directly."""

    def __init__(self, path: str) -> None:
        self._path = path
        self.groups: list[MeasGroup] = []
        self.created_at: MeasTimestamp | None = None
        self._by_name: dict[str, MeasGroup] = {}
        self._read()

    def __enter__(self) -> "MeasReader":
        return self

    def __exit__(self, *args: Any) -> None:
        pass

    def __getitem__(self, name: str) -> MeasGroup:
        if name not in self._by_name:
            raise KeyError(f"Group '{name}' not found")
        return self._by_name[name]

    def _read(self) -> None:
        with open(self._path, "rb") as f:
            data = f.read()

        file_hdr = FileHeader.from_bytes(data)
        self.created_at = MeasTimestamp(file_hdr.created_at_nanos)

        # channel_index → list of (sample_count, raw_bytes)
        channel_chunks: dict[int, list[tuple[int, bytes]]] = {}
        group_defs: list[GroupDef] = []

        offset = file_hdr.first_segment_offset
        while 0 < offset < len(data):
            if offset + SEGMENT_HEADER_SIZE > len(data):
                break
            seg = SegmentHeader.from_bytes(data[offset:])
            content_start = offset + SEGMENT_HEADER_SIZE
            content_end = content_start + seg.content_length
            if content_end > len(data):
                break
            content = bytes(data[content_start:content_end])

            # Decompress if segment is compressed (§4a)
            comp_type = seg.flags & 0x0F
            if comp_type == 1:  # LZ4
                import lz4.block
                content = lz4.block.decompress(content)
            elif comp_type == 2:  # Zstd
                import zstandard
                dctx = zstandard.ZstdDecompressor()
                content = dctx.decompress(content)

            if seg.type == SegmentType.METADATA:
                group_defs = decode_metadata(content)
            elif seg.type == SegmentType.DATA:
                pos = 0
                # Data content begins with [int32: chunkCount]
                (chunk_count,) = struct.unpack_from("<i", content, pos)
                pos += 4
                for _ in range(chunk_count):
                    ch_idx, sample_count, data_len, pos = decode_chunk_header(content, pos)
                    raw = content[pos : pos + data_len]
                    pos += data_len
                    channel_chunks.setdefault(ch_idx, []).append((sample_count, raw))

            next_off = seg.next_segment_offset
            if next_off <= offset:
                break
            offset = next_off

        # Build groups/channels from defs + accumulated chunk data
        global_idx = 0
        for gdef in group_defs:
            channels = []
            for chdef in gdef.channels:
                chunks = channel_chunks.get(global_idx, [])
                channels.append(MeasChannel(chdef.name, chdef.data_type, chdef.properties, chunks))
                global_idx += 1
            grp = MeasGroup(gdef.name, gdef.properties, channels)
            self.groups.append(grp)
            self._by_name[gdef.name] = grp
