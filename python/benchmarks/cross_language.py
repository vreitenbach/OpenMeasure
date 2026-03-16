"""Cross-language MeasFlow benchmarks.

Writes and reads the same workload in Python, then reads files generated
by other language bindings (if present) to compare read performance.

Run:
    python benchmarks/cross_language.py
"""

from __future__ import annotations

import os
import statistics
import shutil
import tempfile
import time

import numpy as np

from measflow.types import MeasDataType
from measflow.writer import MeasWriter
from measflow.reader import MeasReader


def _bench(fn, *, warmup: int = 1, iterations: int = 5) -> dict:
    for _ in range(warmup):
        fn()
    times = []
    for _ in range(iterations):
        t0 = time.perf_counter()
        fn()
        times.append(time.perf_counter() - t0)
    return {
        "median_ms": statistics.median(times) * 1000,
        "min_ms": min(times) * 1000,
        "max_ms": max(times) * 1000,
    }


def main():
    sample_counts = [100_000, 1_000_000]
    tmp_dir = tempfile.mkdtemp(prefix="meas_xlang_")

    try:
        for n in sample_counts:
            data = np.random.default_rng(42).random(n, dtype=np.float32) * 10000

            print(f"\n{'=' * 60}")
            print(f"  Cross-language benchmarks (Python) -- {n} samples")
            print(f"{'=' * 60}")

            # Write benchmark
            path = os.path.join(tmp_dir, f"py_{n}.meas")

            def write_fn():
                with MeasWriter(path) as w:
                    g = w.add_group("Data")
                    ch = g.add_channel("Signal", MeasDataType.Float32)
                    ch.write_bulk(data)

            r = _bench(write_fn)
            print(f"\n  Write (Python):    {r['median_ms']:8.2f} ms")

            # Streaming write
            def stream_fn():
                p = os.path.join(tmp_dir, f"py_stream_{n}.meas")
                chunk = n // 10
                with MeasWriter(p) as w:
                    g = w.add_group("Data")
                    ch = g.add_channel("Signal", MeasDataType.Float32)
                    for i in range(10):
                        ch.write_bulk(data[i * chunk : (i + 1) * chunk])
                        w.flush()

            r = _bench(stream_fn)
            print(f"  Stream (Python):   {r['median_ms']:8.2f} ms  (10 flushes)")

            # Read benchmark (read file we just wrote)
            def read_fn():
                with MeasReader(path) as reader:
                    return reader["Data"]["Signal"].read_all()

            r = _bench(read_fn)
            print(f"  Read (Python):     {r['median_ms']:8.2f} ms")

            # File size
            size_kb = os.path.getsize(path) / 1024
            raw_kb = n * 4 / 1024
            overhead = (size_kb - raw_kb) / raw_kb * 100
            print(f"\n  File size:         {size_kb:8.1f} KB  (overhead: {overhead:.1f}% vs raw)")

            # Cross-read: try to read files from other languages if present
            cross_files = {
                "C#": os.path.join(tmp_dir, f"csharp_{n}.meas"),
                "C": os.path.join(tmp_dir, f"c_{n}.meas"),
            }
            found_cross = False
            for lang, cpath in cross_files.items():
                if os.path.exists(cpath):
                    r = _bench(lambda: MeasReader(cpath)["Data"]["Signal"].read_all())
                    print(f"  Read {lang} file:    {r['median_ms']:8.2f} ms")
                    found_cross = True

            if not found_cross:
                print("\n  (No cross-language files found. Generate with C#/C benchmarks first.)")

    finally:
        shutil.rmtree(tmp_dir, ignore_errors=True)


if __name__ == "__main__":
    main()
