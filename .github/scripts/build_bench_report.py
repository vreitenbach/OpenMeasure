#!/usr/bin/env python3
"""Build a clean benchmark report from CI outputs.

Reads:
  - BenchmarkDotNet markdown artifacts (csharp_xlang.md, csharp_fmtcmp.md)
  - Python benchmark stdout (python_xlang.txt, python_fmtcmp.txt)
  - C benchmark stdout (c_xlang.txt, c_fmtcmp.txt)

Writes: bench_report.md
"""
import re
from pathlib import Path


def read(name: str) -> str:
    p = Path(name)
    return p.read_text(errors="replace").strip() if p.exists() else ""


def extract_bdn_table(md: str) -> str:
    """Extract just the markdown table from BenchmarkDotNet GitHub exporter output."""
    lines = []
    for line in md.splitlines():
        if line.startswith("|"):
            lines.append(line)
    return "\n".join(lines)


def extract_overview_row(py_txt: str, c_txt: str, cs_md: str, samples: str = "100000") -> dict:
    """Extract write/read/stream times for the overview table."""
    row = {"Write": {}, "Read": {}, "Stream": {}}

    # Python — only take values from the 100K section (first occurrence)
    in_target = False
    py_found = set()
    for line in py_txt.splitlines():
        if f"{int(samples):,}" in line or f"{samples}" in line:
            in_target = True
            py_found = set()
        elif re.search(r"\d{1,3}(,\d{3})+\s+samples", line) and in_target:
            break  # moved to next sample count section
        if in_target:
            m = re.match(r"\s+(Write|Read|Stream)\s+\(Python\):\s+([\d.]+)\s+ms", line)
            if m and m.group(1) not in py_found:
                op = m.group(1)
                if op in row:
                    row[op]["Python"] = float(m.group(2))
                    py_found.add(op)

    # C — only take values from the 100K section (first occurrence)
    in_target = False
    c_found = set()
    for line in c_txt.splitlines():
        if samples in line:
            in_target = True
            c_found = set()
        elif re.search(r"\d{4,}\s+samples", line) and in_target:
            break
        if in_target:
            m = re.match(r"\s+(Write|Read|Stream)\s+\(C\):\s+([\d.]+)\s+ms", line)
            if m and m.group(1) not in c_found:
                op = m.group(1)
                if op in row:
                    row[op]["C"] = float(m.group(2))
                    c_found.add(op)

    # C# from BenchmarkDotNet table
    for line in cs_md.splitlines():
        if not line.startswith("|") or "Method" in line or "---" in line:
            continue
        if samples not in line:
            continue
        cols = [c.strip() for c in line.split("|") if c.strip()]
        if len(cols) < 3:
            continue
        desc = cols[0].strip("'")
        # Find the Mean column (first value with us/ms/ns unit)
        for col in cols[1:]:
            m = re.match(r"([\d,.]+)\s+(us|ms|ns)", col)
            if m:
                val = float(m.group(1).replace(",", ""))
                unit = m.group(2)
                ms = val / 1000 if unit == "us" else val if unit == "ms" else val / 1e6
                if "Write" == desc:
                    row["Write"]["C#"] = ms
                elif "Read" == desc:
                    row["Read"]["C#"] = ms
                elif "Stream" in desc:
                    row["Stream"]["C#"] = ms
                break

    return row


def extract_hdf5_comparison(py_txt: str, c_txt: str, cs_md: str) -> list:
    """Extract MeasFlow vs HDF5 write times for the overview."""
    rows = []

    # C# from BenchmarkDotNet format comparison
    cs_mf = cs_hdf = None
    for line in cs_md.splitlines():
        if not line.startswith("|") or "Method" in line or "---" in line:
            continue
        if "100000" not in line:
            continue
        if "Write 1ch" not in line:
            continue
        cols = [c.strip() for c in line.split("|") if c.strip()]
        desc = cols[0].strip("'")
        for col in cols[1:]:
            m = re.match(r"([\d,.]+)\s+(us|ms|ns)", col)
            if m:
                val = float(m.group(1).replace(",", ""))
                unit = m.group(2)
                ms = val / 1000 if unit == "us" else val if unit == "ms" else val / 1e6
                if "HDF5" not in desc and "Raw" not in desc:
                    cs_mf = ms
                elif "HDF5" in desc:
                    cs_hdf = ms
                break
    if cs_mf and cs_hdf:
        rows.append(("C#", cs_mf, cs_hdf))

    # Python — only from 100K section
    in_100k = False
    section = ""
    py_mf = py_hdf = None
    for line in py_txt.splitlines():
        if "100,000" in line or "100000" in line:
            in_100k = True
        elif re.search(r"1[,.]000[,.]000", line):
            in_100k = False
        if not in_100k:
            continue
        if "Write 1 channel" in line:
            section = "write1"
            py_mf = py_hdf = None
        elif "Write 10" in line or "Read" in line or "Stream" in line or "File size" in line:
            if section == "write1" and py_mf and py_hdf:
                break
            section = ""
        if section == "write1":
            m = re.match(r"\s+MeasFlow\s+([\d.]+)\s+ms", line)
            if m:
                py_mf = float(m.group(1))
            m = re.match(r"\s+HDF5.*?\s+([\d.]+)\s+ms", line)
            if m:
                py_hdf = float(m.group(1))
    if py_mf and py_hdf:
        rows.append(("Python", py_mf, py_hdf))

    # C
    section = ""
    c_mf = c_hdf = None
    for line in c_txt.splitlines():
        if "Write 1ch" in line:
            section = "write1"
            c_mf = c_hdf = None
        elif re.match(r"\s*-{10}", line):
            section = ""
        if section == "write1":
            m = re.match(r"\s+MeasFlow:\s+([\d.]+)\s+ms", line)
            if m:
                c_mf = float(m.group(1))
            m = re.match(r"\s+HDF5.*?:\s+([\d.]+)\s+ms", line)
            if m:
                c_hdf = float(m.group(1))
    if c_mf and c_hdf:
        rows.append(("C", c_mf, c_hdf))

    return rows


def fmt(ms: float) -> str:
    if ms < 1:
        return f"{ms * 1000:.0f} μs"
    return f"{ms:.1f} ms"


def main():
    # Read inputs
    cs_xlang_md = read("csharp_xlang.md")
    cs_fmtcmp_md = read("csharp_fmtcmp.md")
    cs_write_md = read("csharp_write.md")
    cs_read_md = read("csharp_read.md")
    cs_filesize_md = read("csharp_filesize.md")
    py_xlang = read("python_xlang.txt")
    py_fmtcmp = read("python_fmtcmp.txt")
    c_xlang = read("c_xlang.txt")
    c_fmtcmp = read("c_fmtcmp.txt")

    cs_xlang_table = extract_bdn_table(cs_xlang_md)
    cs_fmtcmp_table = extract_bdn_table(cs_fmtcmp_md)
    cs_write_table = extract_bdn_table(cs_write_md)
    cs_read_table = extract_bdn_table(cs_read_md)
    cs_filesize_table = extract_bdn_table(cs_filesize_md)

    out = []
    out.append("## Benchmark Results\n")

    # ── Overview: Cross-Language ──
    overview = extract_overview_row(py_xlang, c_xlang, cs_xlang_table)
    has_overview = any(langs for langs in overview.values())

    if has_overview:
        out.append("### Overview — 100K float32 samples\n")
        out.append("| Operation | C | C# | Python |")
        out.append("|---|---|---|---|")
        for op, label in [("Write", "Write"), ("Read", "Read"), ("Stream", "Streaming Write")]:
            langs = overview[op]
            c = fmt(langs["C"]) if "C" in langs else "—"
            cs = fmt(langs["C#"]) if "C#" in langs else "—"
            py = fmt(langs["Python"]) if "Python" in langs else "—"
            out.append(f"| {label} | {c} | {cs} | {py} |")
        out.append("")

    # ── Overview: vs HDF5 ──
    hdf5_rows = extract_hdf5_comparison(py_fmtcmp, c_fmtcmp, cs_fmtcmp_table)
    if hdf5_rows:
        out.append("### vs HDF5 — Write 1ch, 100K samples\n")
        out.append("| Language | MeasFlow | HDF5 | Ratio |")
        out.append("|---|---|---|---|")
        for lang, mf, hdf in hdf5_rows:
            ratio = mf / hdf if hdf > 0 else 0
            icon = "🟢" if ratio <= 1.5 else "🟡" if ratio <= 3 else "🔴"
            out.append(f"| {lang} | {fmt(mf)} | {fmt(hdf)} | {icon} {ratio:.1f}x |")
        out.append("")

    out.append("> _Numbers are indicative — CI runner performance varies between runs._\n")

    # ── Detailed results ──
    out.append("### Detailed Results\n")

    sections = [
        ("C# Cross-Language", cs_xlang_table, False),
        ("C# Write Benchmarks", cs_write_table, False),
        ("C# Read Benchmarks", cs_read_table, False),
        ("C# File Size", cs_filesize_table, False),
        ("C# vs PureHDF", cs_fmtcmp_table, False),
        ("Python Cross-Language", py_xlang, True),
        ("Python vs h5py", py_fmtcmp, True),
        ("C Cross-Language", c_xlang, True),
        ("C vs libhdf5", c_fmtcmp, True),
    ]

    for title, content, use_code_fence in sections:
        if not content:
            continue
        out.append(f"<details><summary>{title}</summary>\n")
        if use_code_fence:
            out.append("```")
            out.append(content)
            out.append("```")
        else:
            out.append(content)
        out.append("\n</details>\n")

    report = "\n".join(out)
    Path("bench_report.md").write_text(report)
    print(f"Report written ({len(report)} bytes)")


if __name__ == "__main__":
    main()
