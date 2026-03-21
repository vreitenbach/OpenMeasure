"""Cross-language roundtrip tests for MeasFlow.

Writes a standardized reference file and verifies files written by C, C#, and Python.
The reference values must match exactly across all three implementations.
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest

from measflow import MeasReader, MeasWriter, MeasDataType
from measflow.types import MeasValue
from measflow.bus import (
    BusChannelDefinition, CanBusConfig, CanFrameDefinition, SignalDefinition,
    ByteOrder, SignalDataType, FrameDirection, encode_bus_def,
)
from measflow.frames import CanFrame

# ── Reference constants (must match C and C#) ───────────────────────────────

SAMPLE_COUNT = 10_000
FLUSH_INTERVAL = 1_000
BASE_TIMESTAMP_NS = 1_700_000_000_000_000_000
ENGINE_FRAME_ID = 0x100
CAN_FRAME_COUNT = 100

REPO_ROOT = Path(__file__).parents[2]


def expected_rpm(i: int) -> np.float32:
    return np.float32(1000.0 + i * 0.5)


def expected_counter(i: int) -> int:
    return i * 3 - 5000


def expected_timestamp_ns(i: int) -> int:
    return BASE_TIMESTAMP_NS + i * 1_000


def expected_rpm_raw(i: int) -> int:
    return int((3000 + i * 10) / 0.25)


def expected_rpm_physical(i: int) -> float:
    return 3000.0 + i * 10


# ── Writer ───────────────────────────────────────────────────────────────────

def write_reference_file(path: str) -> None:
    """Write the standardized cross-language reference file."""
    with MeasWriter(path) as w:
        # File-level properties
        w.properties["TestSuite"] = "CrossLanguage"

        # Group 1: Analog data
        g = w.add_group("Analog")
        g.properties["SampleRate"] = 1000

        time_ch = g.add_channel("Time", MeasDataType.Timestamp)
        rpm_ch = g.add_channel("RPM", MeasDataType.Float32)
        rpm_ch.properties["Unit"] = "1/min"
        counter_ch = g.add_channel("Counter", MeasDataType.Int32)

        # Group 2: CAN bus — define before first flush
        can_group = w.add_group("CAN_Test")

        # Bus definition as binary property
        sig = SignalDefinition(
            name="EngineRPM", start_bit=0, bit_length=16,
            byte_order=ByteOrder.INTEL, data_type=SignalDataType.UNSIGNED,
            factor=0.25, offset=0.0, unit="rpm",
        )
        frame_def = CanFrameDefinition(
            name="EngineData", frame_id=ENGINE_FRAME_ID,
            payload_length=8, direction=FrameDirection.RX,
        )
        frame_def.signals.append(sig)
        bus_def = BusChannelDefinition(
            bus_config=CanBusConfig(baud_rate=500_000),
            raw_frame_channel_name="RawFrames",
            timestamp_channel_name="Timestamps",
            frames=[frame_def],
        )
        can_group.properties["MEAS.bus_def"] = MeasValue(
            MeasDataType.Binary, encode_bus_def(bus_def)
        )

        ts_ch = can_group.add_channel("Timestamps", MeasDataType.Timestamp)
        raw_ch = can_group.add_channel("RawFrames", MeasDataType.Binary)

        # Write analog data with incremental flush
        for flush in range(SAMPLE_COUNT // FLUSH_INTERVAL):
            start = flush * FLUSH_INTERVAL
            for j in range(FLUSH_INTERVAL):
                i = start + j
                time_ch.write(expected_timestamp_ns(i))
                rpm_ch.write(float(expected_rpm(i)))
                counter_ch.write(expected_counter(i))
            w.flush()

        # Write CAN frames
        for i in range(CAN_FRAME_COUNT):
            ts_ns = BASE_TIMESTAMP_NS + i * 10_000_000  # 10ms intervals
            ts_ch.write(ts_ns)
            rpm_raw = expected_rpm_raw(i)
            payload = bytearray(8)
            payload[0] = rpm_raw & 0xFF
            payload[1] = (rpm_raw >> 8) & 0xFF
            frame = CanFrame(arb_id=ENGINE_FRAME_ID, dlc=8, payload=bytes(payload))
            raw_ch.write(frame.encode())


# ── Verifier ─────────────────────────────────────────────────────────────────

def verify_reference_file(path: str, writer_lang: str) -> None:
    """Verify a cross-language reference file."""
    with MeasReader(path) as r:
        # File property
        if "TestSuite" in r.properties:
            ts = r.properties["TestSuite"]
            val = ts.value if hasattr(ts, "value") else ts
            assert str(val) == "CrossLanguage", f"[{writer_lang}] TestSuite mismatch"

        # Group count
        assert len(r.groups) >= 2, f"[{writer_lang}] Expected >=2 groups"

        # ── Analog group ──
        analog = r["Analog"]
        assert len(analog.channels) == 3

        time_ch = analog["Time"]
        rpm_ch = analog["RPM"]
        counter_ch = analog["Counter"]

        assert time_ch.data_type == MeasDataType.Timestamp
        assert rpm_ch.data_type == MeasDataType.Float32
        assert counter_ch.data_type == MeasDataType.Int32

        assert time_ch.sample_count == SAMPLE_COUNT
        assert rpm_ch.sample_count == SAMPLE_COUNT
        assert counter_ch.sample_count == SAMPLE_COUNT

        # Read all data
        rpm_data = np.asarray(rpm_ch.read_all(), dtype=np.float32)
        counter_data = counter_ch.read_all()
        time_data = time_ch.read_all()

        # Spot-check values
        np.testing.assert_almost_equal(rpm_data[0], expected_rpm(0), decimal=3,
                                       err_msg=f"[{writer_lang}] RPM[0]")
        np.testing.assert_almost_equal(rpm_data[9999], expected_rpm(9999), decimal=3,
                                       err_msg=f"[{writer_lang}] RPM[9999]")
        np.testing.assert_almost_equal(rpm_data[5000], expected_rpm(5000), decimal=3,
                                       err_msg=f"[{writer_lang}] RPM[5000]")

        assert counter_data[0] == expected_counter(0), f"[{writer_lang}] Counter[0]"
        assert counter_data[9999] == expected_counter(9999), f"[{writer_lang}] Counter[9999]"

        # Timestamps — handle both int and MeasTimestamp
        def ts_ns(val):
            return val.nanoseconds if hasattr(val, "nanoseconds") else int(val)

        assert ts_ns(time_data[0]) == expected_timestamp_ns(0), f"[{writer_lang}] Time[0]"
        assert ts_ns(time_data[9999]) == expected_timestamp_ns(9999), f"[{writer_lang}] Time[9999]"

        # Statistics
        stats = rpm_ch.statistics
        assert stats is not None, f"[{writer_lang}] RPM statistics missing"
        assert stats.count == SAMPLE_COUNT
        np.testing.assert_almost_equal(stats.min, float(expected_rpm(0)), decimal=1)
        np.testing.assert_almost_equal(stats.max, float(expected_rpm(9999)), decimal=1)
        # Mean = 1000 + 0.5 * (0+9999)/2 = 3499.75
        np.testing.assert_almost_equal(stats.mean, 3499.75, decimal=0)

        # Channel property
        unit_prop = rpm_ch.properties.get("Unit")
        if unit_prop is not None:
            assert str(unit_prop.value if hasattr(unit_prop, "value") else unit_prop) == "1/min"


# ── Test cases ───────────────────────────────────────────────────────────────

@pytest.fixture
def tmp_meas(tmp_path):
    return str(tmp_path / "ref_python.meas")


def test_python_write_and_read_roundtrip(tmp_meas):
    """Python writes and reads its own reference file."""
    write_reference_file(tmp_meas)
    verify_reference_file(tmp_meas, "Python")


@pytest.mark.skipif(
    not (REPO_ROOT / "ref_csharp.meas").exists(),
    reason="ref_csharp.meas not found",
)
def test_read_csharp_reference():
    """Read reference file written by C#."""
    verify_reference_file(str(REPO_ROOT / "ref_csharp.meas"), "C#")


@pytest.mark.skipif(
    not (REPO_ROOT / "ref_c.meas").exists(),
    reason="ref_c.meas not found",
)
def test_read_c_reference():
    """Read reference file written by C."""
    verify_reference_file(str(REPO_ROOT / "ref_c.meas"), "C")


# ── Standalone: generate ref_python.meas ─────────────────────────────────────

if __name__ == "__main__":
    out = str(REPO_ROOT / "ref_python.meas")
    write_reference_file(out)
    print(f"Wrote {out}")
