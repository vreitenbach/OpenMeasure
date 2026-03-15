"""Tests for the measflow Python reader/writer."""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest

from measflow import MeasReader, MeasWriter, MeasDataType, MeasTimestamp
from measflow.bus import (
    BusChannelDefinition, CanBusConfig, CanFrameDefinition, SignalDefinition,
    ByteOrder, SignalDataType, FrameDirection, encode_bus_def, decode_bus_def,
)
from measflow.frames import CanFrame, LinFrame, FlexRayFrame, EthernetFrame


DEMO_FILE = Path(__file__).parents[2] / "demo_measurement.meas"


@pytest.fixture
def tmp_meas(tmp_path):
    return str(tmp_path / "test.meas")


# ── Roundtrip tests ──────────────────────────────────────────────────────────

def test_roundtrip_float32(tmp_meas):
    data = np.array([1.0, 2.5, -3.14, 0.0, 1e6], dtype=np.float32)
    with MeasWriter(tmp_meas) as w:
        g = w.add_group("Sensors")
        ch = g.add_channel("Voltage", MeasDataType.Float32)
        ch.write_bulk(data.tolist())

    with MeasReader(tmp_meas) as r:
        result = r["Sensors"]["Voltage"].read_all()

    np.testing.assert_array_almost_equal(result, data)


def test_roundtrip_float64(tmp_meas):
    data = np.linspace(0, 1, 100)
    with MeasWriter(tmp_meas) as w:
        g = w.add_group("Data")
        ch = g.add_channel("Signal", MeasDataType.Float64)
        ch.write_bulk(data.tolist())

    with MeasReader(tmp_meas) as r:
        result = r["Data"]["Signal"].read_all()

    np.testing.assert_array_almost_equal(result, data)


def test_roundtrip_int32(tmp_meas):
    data = [-1000, 0, 42, 32767, -32768]
    with MeasWriter(tmp_meas) as w:
        g = w.add_group("G")
        ch = g.add_channel("Count", MeasDataType.Int32)
        ch.write_bulk(data)

    with MeasReader(tmp_meas) as r:
        result = r["G"]["Count"].read_all()

    assert list(np.asarray(result, dtype=np.int32)) == data


def test_roundtrip_timestamp(tmp_meas):
    ts0 = MeasTimestamp.now()
    from datetime import timedelta
    timestamps = [ts0 + timedelta(milliseconds=i) for i in range(5)]

    with MeasWriter(tmp_meas) as w:
        g = w.add_group("Log")
        ch = g.add_channel("Time", MeasDataType.Timestamp)
        ch.write_bulk(timestamps)

    with MeasReader(tmp_meas) as r:
        result = r["Log"]["Time"].read_timestamps()

    assert len(result) == 5
    for expected, actual in zip(timestamps, result):
        assert expected.nanoseconds == actual.nanoseconds


def test_sample_count(tmp_meas):
    with MeasWriter(tmp_meas) as w:
        g = w.add_group("G")
        ch = g.add_channel("X", MeasDataType.Float32)
        for v in range(42):
            ch.write(float(v))

    with MeasReader(tmp_meas) as r:
        assert r["G"]["X"].sample_count == 42


# ── Multiple channels & groups ───────────────────────────────────────────────

def test_multiple_channels(tmp_meas):
    rng = np.random.default_rng(0)
    a = rng.random(50).astype(np.float32)
    b = rng.random(50).astype(np.float64)

    with MeasWriter(tmp_meas) as w:
        g = w.add_group("Motor")
        cha = g.add_channel("RPM", MeasDataType.Float32)
        chb = g.add_channel("Temp", MeasDataType.Float64)
        cha.write_bulk(a.tolist())
        chb.write_bulk(b.tolist())

    with MeasReader(tmp_meas) as r:
        np.testing.assert_array_almost_equal(r["Motor"]["RPM"].read_all(), a)
        np.testing.assert_array_almost_equal(r["Motor"]["Temp"].read_all(), b)


def test_multiple_groups(tmp_meas):
    with MeasWriter(tmp_meas) as w:
        g1 = w.add_group("GroupA")
        g1.add_channel("X", MeasDataType.Float32).write_bulk([1.0, 2.0, 3.0])
        g2 = w.add_group("GroupB")
        g2.add_channel("Y", MeasDataType.Float64).write_bulk([10.0, 20.0])

    with MeasReader(tmp_meas) as r:
        assert len(r.groups) == 2
        assert r["GroupA"]["X"].sample_count == 3
        assert r["GroupB"]["Y"].sample_count == 2


# ── Properties ───────────────────────────────────────────────────────────────

def test_group_properties(tmp_meas):
    with MeasWriter(tmp_meas) as w:
        g = w.add_group("Test")
        g.properties["Operator"] = "Alice"
        g.properties["Run"] = 42
        g.add_channel("V", MeasDataType.Float32).write(1.0)

    with MeasReader(tmp_meas) as r:
        props = r["Test"].properties
        assert props["Operator"].value == "Alice"
        assert props["Run"].value == 42


def test_channel_properties(tmp_meas):
    with MeasWriter(tmp_meas) as w:
        g = w.add_group("G")
        ch = g.add_channel("Voltage", MeasDataType.Float64)
        ch.properties["Unit"] = "V"
        ch.write(3.14)

    with MeasReader(tmp_meas) as r:
        assert r["G"]["Voltage"].properties["Unit"].value == "V"


# ── Variable-size channel types (§7) ────────────────────────────────────────

def test_roundtrip_binary(tmp_meas):
    frames = [b"\x01\x02\x03", b"", b"\xff\xfe"]
    with MeasWriter(tmp_meas) as w:
        g = w.add_group("Bus")
        ch = g.add_channel("Frames", MeasDataType.Binary)
        ch.write_bulk(frames)

    with MeasReader(tmp_meas) as r:
        result = r["Bus"]["Frames"].read_all()

    assert result == frames


def test_roundtrip_utf8string(tmp_meas):
    strings = ["hello", "welt", "üäö"]
    with MeasWriter(tmp_meas) as w:
        g = w.add_group("Log")
        ch = g.add_channel("Messages", MeasDataType.Utf8String)
        ch.write_bulk(strings)

    with MeasReader(tmp_meas) as r:
        result = r["Log"]["Messages"].read_all()

    assert result == strings


# ── Streaming write / chunk-based read (§12) ─────────────────────────────────

def test_streaming_multiple_flushes(tmp_meas):
    """Multiple flush() calls create multiple Data segments; read_all() merges them."""
    with MeasWriter(tmp_meas) as w:
        g = w.add_group("G")
        ch = g.add_channel("X", MeasDataType.Float32)
        ch.write_bulk([1.0, 2.0, 3.0])
        w.flush()
        ch.write_bulk([4.0, 5.0])
        w.flush()
        ch.write(6.0)

    with MeasReader(tmp_meas) as r:
        result = r["G"]["X"].read_all()

    np.testing.assert_array_equal(result, np.array([1, 2, 3, 4, 5, 6], dtype=np.float32))


def test_streaming_segment_count(tmp_meas):
    """SegmentCount in file header is patched correctly: 1 metadata + N data segments."""
    from measflow._codec import FileHeader, FILE_HEADER_SIZE
    with MeasWriter(tmp_meas) as w:
        g = w.add_group("G")
        ch = g.add_channel("X", MeasDataType.Float32)
        ch.write(1.0); w.flush()
        ch.write(2.0); w.flush()
        ch.write(3.0)  # flushed on close

    with open(tmp_meas, "rb") as f:
        hdr = FileHeader.from_bytes(f.read(FILE_HEADER_SIZE))

    assert hdr.segment_count == 4  # 1 metadata + 3 data


def test_chunk_based_read(tmp_meas):
    """read_chunks() yields one array per Data segment written."""
    with MeasWriter(tmp_meas) as w:
        g = w.add_group("G")
        ch = g.add_channel("V", MeasDataType.Float64)
        ch.write_bulk([1.0, 2.0]); w.flush()
        ch.write_bulk([3.0, 4.0, 5.0])

    with MeasReader(tmp_meas) as r:
        chunks = list(r["G"]["V"].read_chunks())

    assert len(chunks) == 2
    np.testing.assert_array_equal(chunks[0], [1.0, 2.0])
    np.testing.assert_array_equal(chunks[1], [3.0, 4.0, 5.0])


# ── Bus metadata (§10) ───────────────────────────────────────────────────────

def test_bus_def_roundtrip():
    """encode_bus_def / decode_bus_def round-trips a CAN bus definition."""
    sig = SignalDefinition(
        name="RPM", start_bit=0, bit_length=16,
        byte_order=ByteOrder.INTEL, data_type=SignalDataType.UNSIGNED,
        factor=0.25, offset=0.0, unit="rpm",
    )
    frame = CanFrameDefinition(
        name="EngineStatus", frame_id=0x1A0,
        payload_length=8, direction=FrameDirection.RX,
        is_extended_id=False,
    )
    frame.signals.append(sig)

    bus_def = BusChannelDefinition(
        bus_config=CanBusConfig(is_extended_id=False, baud_rate=500_000),
        raw_frame_channel_name="RawCAN",
        timestamp_channel_name="Timestamps",
        frames=[frame],
    )
    raw = encode_bus_def(bus_def)
    decoded = decode_bus_def(raw)

    assert isinstance(decoded.bus_config, CanBusConfig)
    assert decoded.bus_config.baud_rate == 500_000
    assert decoded.raw_frame_channel_name == "RawCAN"
    assert len(decoded.frames) == 1
    assert decoded.frames[0].name == "EngineStatus"
    assert decoded.frames[0].frame_id == 0x1A0
    assert len(decoded.frames[0].signals) == 1
    assert decoded.frames[0].signals[0].name == "RPM"
    assert decoded.frames[0].signals[0].factor == 0.25
    assert decoded.frames[0].signals[0].unit == "rpm"


def test_bus_def_stored_in_group_property(tmp_meas):
    """Bus definition survives a write/read cycle via MEAS.bus_def group property."""
    from measflow.types import MeasValue, MeasDataType as MDT
    sig = SignalDefinition(name="Speed", start_bit=0, bit_length=8,
                           byte_order=ByteOrder.INTEL, data_type=SignalDataType.UNSIGNED,
                           factor=1.0, offset=0.0)
    frame = CanFrameDefinition(name="VehicleSpeed", frame_id=0x200,
                                payload_length=2, direction=FrameDirection.RX)
    frame.signals.append(sig)
    bus_def = BusChannelDefinition(
        bus_config=CanBusConfig(baud_rate=250_000),
        raw_frame_channel_name="Raw", timestamp_channel_name="TS",
        frames=[frame],
    )

    with MeasWriter(tmp_meas) as w:
        g = w.add_group("CAN")
        g.properties["MEAS.bus_def"] = MeasValue(MDT.Binary, encode_bus_def(bus_def))
        g.add_channel("Raw", MDT.Binary).write(b"\x00\x00")

    with MeasReader(tmp_meas) as r:
        raw_prop = r["CAN"].properties["MEAS.bus_def"].value
        restored = decode_bus_def(raw_prop)

    assert restored.frames[0].name == "VehicleSpeed"
    assert isinstance(restored.bus_config, CanBusConfig)
    assert restored.bus_config.baud_rate == 250_000


# ── Wire frames (§11) ────────────────────────────────────────────────────────

def test_can_frame_roundtrip():
    f = CanFrame(arb_id=0x1FF, dlc=4, payload=b"\xDE\xAD\xBE\xEF")
    decoded = CanFrame.decode(f.encode())
    assert decoded.arb_id == 0x1FF
    assert decoded.dlc == 4
    assert decoded.payload == b"\xDE\xAD\xBE\xEF"
    assert len(f.encode()) == 10  # 6 + 4


def test_lin_frame_roundtrip():
    f = LinFrame(frame_id=0x20, dlc=6, payload=b"\x01\x02\x03\x04\x05\x06", nad=0x01)
    decoded = LinFrame.decode(f.encode())
    assert decoded.frame_id == 0x20
    assert decoded.dlc == 6
    assert decoded.payload == b"\x01\x02\x03\x04\x05\x06"
    assert decoded.nad == 0x01
    assert len(f.encode()) == 10  # 4 + 6


def test_flexray_frame_roundtrip():
    f = FlexRayFrame(slot_id=5, payload=b"\xAA\xBB\xCC", cycle_count=10, channel_flags=0b11)
    decoded = FlexRayFrame.decode(f.encode())
    assert decoded.slot_id == 5
    assert decoded.payload == b"\xAA\xBB\xCC"
    assert decoded.cycle_count == 10
    assert len(f.encode()) == 9  # 6 + 3


def test_ethernet_frame_roundtrip():
    mac_dst = bytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF])
    mac_src = bytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF])
    f = EthernetFrame(mac_destination=mac_dst, mac_source=mac_src,
                      ether_type=0x0800, payload=b"\x45\x00", vlan_id=0)
    decoded = EthernetFrame.decode(f.encode())
    assert decoded.mac_destination == mac_dst
    assert decoded.mac_source == mac_src
    assert decoded.ether_type == 0x0800
    assert decoded.payload == b"\x45\x00"
    assert len(f.encode()) == 20  # 18 + 2


# ── Interoperability ─────────────────────────────────────────────────────────

@pytest.mark.skipif(not DEMO_FILE.exists(), reason="demo_measurement.meas not found")
def test_read_csharp_demo_file():
    """Read the C#-generated demo file and verify basic structure."""
    with MeasReader(str(DEMO_FILE)) as r:
        assert len(r.groups) >= 1
        motor = r["Motor"]
        assert motor["RPM"].sample_count == 1000
        assert motor["OilTemperature"].sample_count == 1000

        rpm = np.asarray(motor["RPM"].read_all())
        assert len(rpm) == 1000
        # Sanity: RPM values should be in a plausible range
        assert rpm.min() > 2000
        assert rpm.max() < 4000


@pytest.mark.skipif(not DEMO_FILE.exists(), reason="demo_measurement.meas not found")
def test_read_csharp_demo_file_statistics():
    """Statistics written by the C# writer are readable by the Python reader."""
    from measflow import ChannelStatistics
    with MeasReader(str(DEMO_FILE)) as r:
        stats = r["Motor"]["RPM"].statistics
        assert stats is not None
        assert isinstance(stats, ChannelStatistics)
        assert stats.count == 1000
        assert stats.min > 2000
        assert stats.max < 4000
        assert stats.mean > 2000
        assert stats.variance > 0
        assert stats.std_dev > 0


# ── Channel statistics (§13) ─────────────────────────────────────────────────

def test_statistics_basic(tmp_meas):
    """Statistics are computed and stored for numeric channels."""
    data = [1.0, 2.0, 3.0, 4.0, 5.0]
    with MeasWriter(tmp_meas) as w:
        g = w.add_group("G")
        ch = g.add_channel("X", MeasDataType.Float64)
        ch.write_bulk(data)

    with MeasReader(tmp_meas) as r:
        stats = r["G"]["X"].statistics
    assert stats is not None
    assert stats.count == 5
    assert stats.min == pytest.approx(1.0)
    assert stats.max == pytest.approx(5.0)
    assert stats.sum == pytest.approx(15.0)
    assert stats.mean == pytest.approx(3.0)
    assert stats.first == pytest.approx(1.0)
    assert stats.last == pytest.approx(5.0)
    # Population variance of [1,2,3,4,5]: sum of squared deviations from mean / n = 10/5 = 2.0
    assert stats.variance == pytest.approx(2.0)
    import math
    assert stats.std_dev == pytest.approx(math.sqrt(2.0))


def test_statistics_int32(tmp_meas):
    """Statistics work for integer channel types."""
    data = [-10, 0, 10, 20, 30]
    with MeasWriter(tmp_meas) as w:
        g = w.add_group("G")
        ch = g.add_channel("V", MeasDataType.Int32)
        ch.write_bulk(data)

    with MeasReader(tmp_meas) as r:
        stats = r["G"]["V"].statistics
    assert stats is not None
    assert stats.count == 5
    assert stats.min == pytest.approx(-10.0)
    assert stats.max == pytest.approx(30.0)
    assert stats.mean == pytest.approx(10.0)


def test_statistics_single_sample(tmp_meas):
    """Statistics with a single sample: variance = 0."""
    with MeasWriter(tmp_meas) as w:
        g = w.add_group("G")
        ch = g.add_channel("V", MeasDataType.Float32)
        ch.write(7.0)

    with MeasReader(tmp_meas) as r:
        stats = r["G"]["V"].statistics
    assert stats is not None
    assert stats.count == 1
    assert stats.min == pytest.approx(7.0)
    assert stats.max == pytest.approx(7.0)
    assert stats.variance == pytest.approx(0.0)
    assert stats.first == pytest.approx(7.0)
    assert stats.last == pytest.approx(7.0)


def test_statistics_streaming(tmp_meas):
    """Statistics accumulate correctly across multiple flush() calls."""
    with MeasWriter(tmp_meas) as w:
        g = w.add_group("G")
        ch = g.add_channel("V", MeasDataType.Float64)
        ch.write_bulk([1.0, 2.0, 3.0])
        w.flush()
        ch.write_bulk([4.0, 5.0])

    with MeasReader(tmp_meas) as r:
        stats = r["G"]["V"].statistics
    assert stats is not None
    assert stats.count == 5
    assert stats.min == pytest.approx(1.0)
    assert stats.max == pytest.approx(5.0)
    assert stats.mean == pytest.approx(3.0)


def test_statistics_not_available_for_binary(tmp_meas):
    """Binary channels do not have statistics."""
    with MeasWriter(tmp_meas) as w:
        g = w.add_group("G")
        ch = g.add_channel("B", MeasDataType.Binary)
        ch.write(b"\x01\x02")

    with MeasReader(tmp_meas) as r:
        assert r["G"]["B"].statistics is None


def test_statistics_not_available_for_string(tmp_meas):
    """String channels do not have statistics."""
    with MeasWriter(tmp_meas) as w:
        g = w.add_group("G")
        ch = g.add_channel("S", MeasDataType.Utf8String)
        ch.write("hello")

    with MeasReader(tmp_meas) as r:
        assert r["G"]["S"].statistics is None


# ── Compression tests (§4a) ─────────────────────────────────────────────────

@pytest.mark.parametrize("compression", ["lz4", "zstd"])
def test_compression_roundtrip_float(tmp_path, compression):
    path = str(tmp_path / f"comp_{compression}.meas")
    data = np.array([1.0, 2.5, -3.14, 0.0, 1e6], dtype=np.float32)
    with MeasWriter(path, compression=compression) as w:
        ch = w.add_group("G").add_channel("V", MeasDataType.Float32)
        ch.write_bulk(data.tolist())
    with MeasReader(path) as r:
        np.testing.assert_array_almost_equal(r["G"]["V"].read_all(), data)


@pytest.mark.parametrize("compression", ["lz4", "zstd"])
def test_compression_roundtrip_multiple_types(tmp_path, compression):
    path = str(tmp_path / f"multi_{compression}.meas")
    ints = [-1, 0, 42, 2**31 - 1, -(2**31)]
    floats = [3.14, 2.718, 0.0, -999.9, 1e20]
    with MeasWriter(path, compression=compression) as w:
        g = w.add_group("M")
        g.add_channel("I", MeasDataType.Int32).write_bulk(ints)
        g.add_channel("F", MeasDataType.Float64).write_bulk(floats)
    with MeasReader(path) as r:
        np.testing.assert_array_equal(r["M"]["I"].read_all(), ints)
        np.testing.assert_array_almost_equal(r["M"]["F"].read_all(), floats)


@pytest.mark.parametrize("compression", ["lz4", "zstd"])
def test_compression_incremental_flush(tmp_path, compression):
    path = str(tmp_path / f"flush_{compression}.meas")
    with MeasWriter(path, compression=compression) as w:
        ch = w.add_group("G").add_channel("V", MeasDataType.Float64)
        ch.write_bulk([1.0, 2.0])
        w.flush()
        ch.write_bulk([3.0, 4.0])
        w.flush()
        ch.write(5.0)
    with MeasReader(path) as r:
        result = r["G"]["V"].read_all()
    np.testing.assert_array_almost_equal(result, [1.0, 2.0, 3.0, 4.0, 5.0])


@pytest.mark.parametrize("compression", ["lz4", "zstd"])
def test_compression_smaller_than_uncompressed(tmp_path, compression):
    path_none = str(tmp_path / "none.meas")
    path_comp = str(tmp_path / f"comp_{compression}.meas")
    data = [(i % 100) * 1.0 for i in range(50_000)]
    for path, comp in [(path_none, "none"), (path_comp, compression)]:
        with MeasWriter(path, compression=comp) as w:
            w.add_group("G").add_channel("V", MeasDataType.Float64).write_bulk(data)
    size_none = Path(path_none).stat().st_size
    size_comp = Path(path_comp).stat().st_size
    assert size_comp < size_none, f"{compression}: {size_comp} >= {size_none}"


@pytest.mark.parametrize("compression", ["lz4", "zstd"])
def test_compression_statistics_preserved(tmp_path, compression):
    path = str(tmp_path / f"stats_{compression}.meas")
    data = [10.0, 20.0, 30.0, 40.0, 50.0]
    with MeasWriter(path, compression=compression) as w:
        w.add_group("G").add_channel("V", MeasDataType.Float64).write_bulk(data)
    with MeasReader(path) as r:
        stats = r["G"]["V"].statistics
    assert stats is not None
    assert stats.count == 5
    assert stats.min == pytest.approx(10.0)
    assert stats.max == pytest.approx(50.0)
    assert stats.mean == pytest.approx(30.0)


@pytest.mark.parametrize("compression", ["lz4", "zstd"])
def test_compression_binary_frames(tmp_path, compression):
    path = str(tmp_path / f"bin_{compression}.meas")
    frames = [b"\x01\x02\x03", b"\xDE\xAD\xBE\xEF", b"\xFF"]
    with MeasWriter(path, compression=compression) as w:
        ch = w.add_group("B").add_channel("F", MeasDataType.Binary)
        for f in frames:
            ch.write(f)
    with MeasReader(path) as r:
        result = r["B"]["F"].read_all()
    assert len(result) == len(frames)
    for expected, actual in zip(frames, result):
        assert expected == actual
