"""MeasFlow Python quickstart — write and read back a .meas file.

Install measflow first:
    pip install -e ..

Run:
    python quickstart.py
"""

import math
import os
import sys
from datetime import timedelta

# Allow running directly without installing the package
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from measflow import MeasDataType, MeasReader, MeasTimestamp, MeasWriter

OUTPUT = "quickstart.meas"
N = 100


# ── Write ──────────────────────────────────────────────────────────────────
print("=== Writing measurement data ===")

with MeasWriter(OUTPUT) as writer:
    motor = writer.add_group("Motor")
    motor.properties["Operator"] = "Alice"
    motor.properties["Testbench"] = "P42"

    rpm = motor.add_channel("RPM", MeasDataType.Float32)
    rpm.properties["Unit"] = "1/min"

    temp = motor.add_channel("OilTemperature", MeasDataType.Float64)
    temp.properties["Unit"] = "°C"

    time_ch = motor.add_channel("Time", MeasDataType.Timestamp)

    start = MeasTimestamp.now()
    for i in range(N):
        time_ch.write(start + timedelta(milliseconds=i))
        rpm.write(3000.0 + math.sin(i * 0.1) * 500)
        temp.write(90.0 + math.sin(i * 0.05) * 5.0)

print(f"  Written: {OUTPUT}  ({N} samples per channel)")


# ── Read ───────────────────────────────────────────────────────────────────
print("\n=== Reading measurement data ===")

with MeasReader(OUTPUT) as reader:
    print(f"  Created at : {reader.created_at}")
    print(f"  Groups     : {len(reader.groups)}")

    motor = reader["Motor"]
    print(f"\n  Group '{motor.name}'  ({len(motor.channels)} channels)")

    rpm_data = motor["RPM"].read_all()
    temp_data = motor["OilTemperature"].read_all()
    timestamps = motor["Time"].read_timestamps()

    print(f"    RPM            : {len(rpm_data)} samples, "
          f"first={rpm_data[0]:.1f}, last={rpm_data[-1]:.1f}")
    print(f"    OilTemperature : {len(temp_data)} samples, "
          f"first={temp_data[0]:.1f}")
    print(f"    Time range     : {timestamps[0]}  →  {timestamps[-1]}")

    # Pre-computed statistics — no re-reading needed
    stats = motor["RPM"].statistics
    if stats:
        print(f"\n  RPM statistics (pre-computed, zero re-read cost):")
        print(f"    count={stats.count}  min={stats.min:.1f}  "
              f"max={stats.max:.1f}  mean={stats.mean:.1f}  "
              f"std_dev={stats.std_dev:.2f}")

file_size = os.path.getsize(OUTPUT)
print(f"\n  File size: {file_size:,} bytes ({file_size / 1024:.1f} KB)")
print("\nDone.")
