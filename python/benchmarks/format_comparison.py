"""Format comparison benchmarks: MeasFlow vs HDF5 (h5py).

Run:
    pip install h5py
    python benchmarks/format_comparison.py
"""

from __future__ import annotations

import os
import shutil
import statistics
import tempfile
import time

import numpy as np

from measflow.types import MeasDataType
from measflow.writer import MeasWriter
from measflow.reader import MeasReader

try:
    import h5py

    HAS_H5PY = True
except ImportError:
    HAS_H5PY = False


def _bench(fn, *, warmup: int = 1, iterations: int = 5) -> dict:
    """Run *fn* and return timing statistics."""
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


class BenchmarkSuite:
    def __init__(self, sample_count: int = 1_000_000, tmp_dir: str | None = None):
        self.sample_count = sample_count
        self.tmp_dir = tmp_dir or tempfile.mkdtemp(prefix="meas_bench_")
        self.data = np.random.default_rng(42).random(sample_count, dtype=np.float32) * 10000
        self._prepare_read_files()

    def _prepare_read_files(self):
        self.meas_file = os.path.join(self.tmp_dir, "read.meas")
        self._write_measflow(self.meas_file, self.data)
        if HAS_H5PY:
            self.h5_file = os.path.join(self.tmp_dir, "read.h5")
            self._write_h5(self.h5_file, self.data)

    def cleanup(self):
        shutil.rmtree(self.tmp_dir, ignore_errors=True)

    # -- MeasFlow helpers -------------------------------------------------

    def _write_measflow(self, path: str, data: np.ndarray):
        with MeasWriter(path) as w:
            g = w.add_group("Data")
            ch = g.add_channel("Signal", MeasDataType.Float32, track_statistics=False)
            ch.write_bulk(data)

    def _read_measflow(self, path: str) -> np.ndarray:
        with MeasReader(path) as r:
            return r["Data"]["Signal"].read_all()

    # -- HDF5 helpers -----------------------------------------------------

    def _write_h5(self, path: str, data: np.ndarray):
        with h5py.File(path, "w") as f:
            grp = f.create_group("Data")
            grp.create_dataset("Signal", data=data)

    def _read_h5(self, path: str) -> np.ndarray:
        with h5py.File(path, "r") as f:
            return f["Data"]["Signal"][:]

    # -- Benchmarks -------------------------------------------------------

    def bench_write_1ch(self) -> dict:
        results = {}
        path = os.path.join(self.tmp_dir, "w1.meas")
        results["MeasFlow"] = _bench(lambda: self._write_measflow(path, self.data))
        results["MeasFlow"]["size_kb"] = os.path.getsize(path) / 1024
        if HAS_H5PY:
            path_h5 = os.path.join(self.tmp_dir, "w1.h5")
            results["HDF5 (h5py)"] = _bench(lambda: self._write_h5(path_h5, self.data))
            results["HDF5 (h5py)"]["size_kb"] = os.path.getsize(path_h5) / 1024
        return results

    def bench_write_10ch(self) -> dict:
        results = {}

        def write_meas_10ch():
            p = os.path.join(self.tmp_dir, "w10.meas")
            with MeasWriter(p) as w:
                g = w.add_group("Data")
                for c in range(10):
                    ch = g.add_channel(f"Ch{c}", MeasDataType.Float32, track_statistics=False)
                    ch.write_bulk(self.data)

        results["MeasFlow"] = _bench(write_meas_10ch)

        if HAS_H5PY:
            def write_h5_10ch():
                p = os.path.join(self.tmp_dir, "w10.h5")
                with h5py.File(p, "w") as f:
                    grp = f.create_group("Data")
                    for c in range(10):
                        grp.create_dataset(f"Ch{c}", data=self.data)

            results["HDF5 (h5py)"] = _bench(write_h5_10ch)
        return results

    def bench_read_1ch(self) -> dict:
        results = {}
        results["MeasFlow"] = _bench(lambda: self._read_measflow(self.meas_file))
        if HAS_H5PY:
            results["HDF5 (h5py)"] = _bench(lambda: self._read_h5(self.h5_file))
        return results

    def bench_read_10ch(self) -> dict:
        results = {}

        # Prepare 10-channel files
        meas10 = os.path.join(self.tmp_dir, "r10.meas")
        with MeasWriter(meas10) as w:
            g = w.add_group("Data")
            for c in range(10):
                ch = g.add_channel(f"Ch{c}", MeasDataType.Float32, track_statistics=False)
                ch.write_bulk(self.data)

        def read_meas_10ch():
            with MeasReader(meas10) as r:
                for c in range(10):
                    r["Data"][f"Ch{c}"].read_all()

        results["MeasFlow"] = _bench(read_meas_10ch)

        if HAS_H5PY:
            h510 = os.path.join(self.tmp_dir, "r10.h5")
            with h5py.File(h510, "w") as f:
                grp = f.create_group("Data")
                for c in range(10):
                    grp.create_dataset(f"Ch{c}", data=self.data)

            def read_h5_10ch():
                with h5py.File(h510, "r") as f:
                    for c in range(10):
                        f["Data"][f"Ch{c}"][:]

            results["HDF5 (h5py)"] = _bench(read_h5_10ch)
        return results

    def bench_streaming_write(self) -> dict:
        results = {}
        chunk_size = self.sample_count // 10

        def stream_meas():
            p = os.path.join(self.tmp_dir, "stream.meas")
            with MeasWriter(p) as w:
                g = w.add_group("Data")
                ch = g.add_channel("Signal", MeasDataType.Float32, track_statistics=False)
                for i in range(10):
                    ch.write_bulk(self.data[i * chunk_size : (i + 1) * chunk_size])
                    w.flush()

        # HDF5 has no streaming support — MeasFlow only
        results["MeasFlow (10 flushes)"] = _bench(stream_meas)
        return results

    def bench_file_size(self) -> dict:
        results = {}
        p_meas = os.path.join(self.tmp_dir, "size.meas")
        self._write_measflow(p_meas, self.data)
        results["MeasFlow"] = {"size_kb": os.path.getsize(p_meas) / 1024}

        if HAS_H5PY:
            p_h5 = os.path.join(self.tmp_dir, "size.h5")
            self._write_h5(p_h5, self.data)
            results["HDF5 (h5py)"] = {"size_kb": os.path.getsize(p_h5) / 1024}

        p_raw = os.path.join(self.tmp_dir, "size.bin")
        with open(p_raw, "wb") as f:
            f.write(self.data.tobytes())
        results["Raw binary"] = {"size_kb": os.path.getsize(p_raw) / 1024}
        return results


def _print_results(name: str, results: dict):
    print(f"\n{'-' * 60}")
    print(f"  {name}")
    print(f"{'-' * 60}")
    for label, data in results.items():
        if "median_ms" in data:
            print(f"  {label}: {data['median_ms']:8.2f} ms")
        elif "size_kb" in data:
            print(f"  {label}: {data['size_kb']:8.1f} KB")


def main():
    for n in [100_000, 1_000_000]:
        print(f"\n{'=' * 60}")
        print(f"  Format comparison (Python) -- {n} samples")
        print(f"{'=' * 60}")

        suite = BenchmarkSuite(sample_count=n)
        try:
            _print_results("Write 1 channel", suite.bench_write_1ch())
            _print_results("Write 10 channels", suite.bench_write_10ch())
            _print_results("Read 1 channel", suite.bench_read_1ch())
            _print_results("Read 10 channels", suite.bench_read_10ch())
            _print_results("Streaming write", suite.bench_streaming_write())
            _print_results("File size", suite.bench_file_size())
        finally:
            suite.cleanup()

    if not HAS_H5PY:
        print("\nWarning: h5py not installed -- only MeasFlow results shown.")
        print("  Install with: pip install h5py")


if __name__ == "__main__":
    main()
