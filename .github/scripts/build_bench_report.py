#!/usr/bin/env python3
"""Build a clean benchmark report from CI outputs.

All three language bindings (C#, Python, C) output the same standardized
plain-text format, making parsing trivial.

Reads:
  - csharp_xlang.txt, csharp_fmtcmp.txt
  - python_xlang.txt, python_fmtcmp.txt
  - c_xlang.txt, c_fmtcmp.txt

Writes: bench_report.md
"""
import re
from pathlib import Path


def read(name: str) -> str:
    p = Path(name)
    return p.read_text(errors="replace").strip() if p.exists() else ""


# ── Unified parser ────────────────────────────────────────────────────────────


def parse_cross_language(text: str) -> dict:
    """Parse cross-language benchmark output.

    Returns: {sample_count: {"Write": ms, "Stream": ms, "Read": ms, "FileSize": kb}}
    """
    result = {}
    current_samples = None

    for line in text.splitlines():
        # Header: "Cross-language benchmarks (C#) -- 100000 samples"
        m = re.search(r"--\s+(\d+)\s+samples", line)
        if m:
            current_samples = int(m.group(1))
            result[current_samples] = {}
            continue

        if current_samples is None:
            continue

        # "  Write (C#):        12.34 ms"
        m = re.match(r"\s+(Write|Stream|Read)\s+\(.+?\):\s+([\d.]+)\s+ms", line)
        if m:
            result[current_samples][m.group(1)] = float(m.group(2))
            continue

        # "  File size:         390.7 KB"
        m = re.match(r"\s+File size:\s+([\d.]+)\s+KB", line)
        if m:
            result[current_samples]["FileSize"] = float(m.group(1))

    return result


def parse_format_comparison(text: str) -> dict:
    """Parse format comparison benchmark output.

    Returns: {sample_count: {section: {label: {"ms": float} or {"kb": float}}}}
    """
    result = {}
    current_samples = None
    current_section = None

    for line in text.splitlines():
        # Header: "Format comparison (C#) -- 100000 samples"
        m = re.search(r"--\s+(\d+)\s+samples", line)
        if m:
            current_samples = int(m.group(1))
            result[current_samples] = {}
            current_section = None
            continue

        if current_samples is None:
            continue

        # Skip dashed separator lines
        if re.match(r"-{10,}", line.strip()):
            continue

        # Section title (indented text line without a colon-number pattern)
        m = re.match(r"^\s{2}(\w.+)$", line)
        if m and not re.search(r":\s+[\d.]", line):
            title = m.group(1).strip()
            if title not in result[current_samples]:
                current_section = title
                result[current_samples][current_section] = {}
            continue

        if current_section is None:
            continue

        # Result: "  MeasFlow:          12.34 ms"
        m = re.match(r"\s+(.+?):\s+([\d.]+)\s+ms", line)
        if m:
            result[current_samples][current_section][m.group(1).strip()] = {"ms": float(m.group(2))}
            continue

        # Size: "  MeasFlow:          390.7 KB"
        m = re.match(r"\s+(.+?):\s+([\d.]+)\s+KB", line)
        if m:
            result[current_samples][current_section][m.group(1).strip()] = {"kb": float(m.group(2))}

    return result


# ── Formatting helpers ────────────────────────────────────────────────────────


def fmt_ms(ms: float) -> str:
    if ms < 1:
        return f"{ms * 1000:.0f} \u03bcs"
    return f"{ms:.1f} ms"


def fmt_kb(kb: float) -> str:
    if kb >= 1024:
        return f"{kb / 1024:.1f} MB"
    return f"{kb:.0f} KB"


# ── Report builder ────────────────────────────────────────────────────────────


def main():
    # Read all inputs
    inputs = {
        "C#": (read("csharp_xlang.txt"), read("csharp_fmtcmp.txt")),
        "Python": (read("python_xlang.txt"), read("python_fmtcmp.txt")),
        "C": (read("c_xlang.txt"), read("c_fmtcmp.txt")),
    }

    # Parse
    xlang = {lang: parse_cross_language(txt) for lang, (txt, _) in inputs.items() if txt}
    fmtcmp = {lang: parse_format_comparison(txt) for lang, (_, txt) in inputs.items() if txt}

    missing = [lang for lang, (x, f) in inputs.items() if not x and not f]
    found = [lang for lang, (x, f) in inputs.items() if x or f]

    out = []
    out.append("## Benchmark Results\n")

    if missing:
        out.append(f"> \u26a0\ufe0f Missing data for: {', '.join(missing)}\n")

    # ── Overview: Cross-Language (100K) ──
    n = 100_000
    has_xlang = any(n in data for data in xlang.values())

    if has_xlang:
        out.append(f"### Cross-Language \u2014 {n:,} float32 samples\n")
        out.append("> With statistics tracking enabled\n")
        out.append("| Operation | C | C# | Python |")
        out.append("|---|---|---|---|")
        for op, label in [("Write", "Write"), ("Read", "Read"), ("Stream", "Streaming Write")]:
            c = fmt_ms(xlang["C"][n][op]) if "C" in xlang and n in xlang["C"] and op in xlang["C"][n] else "\u2014"
            cs = fmt_ms(xlang["C#"][n][op]) if "C#" in xlang and n in xlang["C#"] and op in xlang["C#"][n] else "\u2014"
            py = fmt_ms(xlang["Python"][n][op]) if "Python" in xlang and n in xlang["Python"] and op in xlang["Python"][n] else "\u2014"
            out.append(f"| {label} | {c} | {cs} | {py} |")
        out.append("")

    # ── vs HDF5: all operations (100K) ──
    hdf5_ops = [
        ("Write 1 channel", "Write 1ch"),
        ("Write 10 channels", "Write 10ch"),
        ("Read 1 channel", "Read 1ch"),
        ("Read 10 channels", "Read 10ch"),
    ]
    hdf5_table_rows = []
    for section_key, label in hdf5_ops:
        row = {"label": label}
        for lang in ["C", "C#", "Python"]:
            if lang not in fmtcmp or n not in fmtcmp[lang]:
                continue
            section = fmtcmp[lang][n].get(section_key, {})
            mf = next((v for k, v in section.items() if "MeasFlow" in k), None)
            hdf = next((v for k, v in section.items() if "HDF5" in k), None)
            if mf and hdf and "ms" in mf and "ms" in hdf:
                ratio = mf["ms"] / hdf["ms"] if hdf["ms"] > 0 else 0
                icon = "\U0001f7e2" if ratio <= 1.5 else "\U0001f7e1" if ratio <= 3 else "\U0001f534"
                row[lang] = f"{icon} {ratio:.1f}x ({fmt_ms(mf['ms'])})"
            elif mf and "ms" in mf:
                row[lang] = fmt_ms(mf["ms"])
        if any(lang in row for lang in ["C", "C#", "Python"]):
            hdf5_table_rows.append(row)

    if hdf5_table_rows:
        out.append(f"### vs HDF5 \u2014 {n:,} samples\n")
        out.append("> Without statistics tracking \u2014 pure I/O comparison\n")
        out.append("> Ratio = MeasFlow / HDF5 \u2014 lower is better "
                   "(\U0001f7e2 \u22641.5x, \U0001f7e1 \u22643x, \U0001f534 >3x)\n")
        out.append("| Operation | C | C# | Python |")
        out.append("|---|---|---|---|")
        for row in hdf5_table_rows:
            c = row.get("C", "\u2014")
            cs = row.get("C#", "\u2014")
            py = row.get("Python", "\u2014")
            out.append(f"| {row['label']} | {c} | {cs} | {py} |")
        out.append("")

    # ── Streaming write (MeasFlow only — HDF5 has no streaming) ──
    stream_row = {}
    for lang in ["C", "C#", "Python"]:
        if lang not in fmtcmp or n not in fmtcmp[lang]:
            continue
        section = fmtcmp[lang][n].get("Streaming write", {})
        mf = next((v for k, v in section.items() if "MeasFlow" in k), None)
        if mf and "ms" in mf:
            stream_row[lang] = fmt_ms(mf["ms"])
    if stream_row:
        out.append(f"### Streaming Write \u2014 {n:,} samples, 10 flushes\n")
        out.append("> MeasFlow-exclusive feature \u2014 HDF5 has no streaming support\n")
        out.append("| | C | C# | Python |")
        out.append("|---|---|---|---|")
        c = stream_row.get("C", "\u2014")
        cs = stream_row.get("C#", "\u2014")
        py = stream_row.get("Python", "\u2014")
        out.append(f"| 10\u00d7 flush | {c} | {cs} | {py} |")
        out.append("")

    # ── File size comparison (100K) ──
    size_rows = []
    raw_kb = None
    for lang in ["C", "C#", "Python"]:
        if lang not in fmtcmp or n not in fmtcmp[lang]:
            continue
        size_section = fmtcmp[lang][n].get("File size", {})
        mf_entry = next((v for k, v in size_section.items() if "MeasFlow" in k), None)
        hdf_entry = next((v for k, v in size_section.items() if "HDF5" in k), None)
        raw_entry = next((v for k, v in size_section.items() if "raw" in k.lower()), None)
        if raw_entry and "kb" in raw_entry and raw_kb is None:
            raw_kb = raw_entry["kb"]
        if mf_entry and "kb" in mf_entry:
            size_rows.append((lang, mf_entry.get("kb"), hdf_entry.get("kb") if hdf_entry else None))

    if size_rows:
        out.append(f"### File Size \u2014 1ch, {n:,} samples\n")
        out.append("| Language | MeasFlow | HDF5 | Overhead |")
        out.append("|---|---|---|---|")
        for lang, mf, hdf in size_rows:
            mf_s = fmt_kb(mf) if mf else "\u2014"
            hdf_s = fmt_kb(hdf) if hdf else "\u2014"
            overhead = f"{(mf - raw_kb) / raw_kb * 100:.1f}%" if mf and raw_kb else "\u2014"
            out.append(f"| {lang} | {mf_s} | {hdf_s} | {overhead} |")
        if raw_kb:
            out.append(f"\n> Raw data: {fmt_kb(raw_kb)} ({n:,} \u00d7 4 bytes)")
        out.append("")

    out.append("> _Numbers are indicative \u2014 CI runner performance varies between runs._\n")

    # ── Detailed results (collapsible) ──
    out.append("### Detailed Results\n")

    sections = [
        ("C# Cross-Language", inputs["C#"][0]),
        ("C# Format Comparison", inputs["C#"][1]),
        ("Python Cross-Language", inputs["Python"][0]),
        ("Python Format Comparison", inputs["Python"][1]),
        ("C Cross-Language", inputs["C"][0]),
        ("C Format Comparison", inputs["C"][1]),
    ]

    for title, content in sections:
        if not content:
            continue
        out.append(f"<details><summary>{title}</summary>\n")
        out.append("```")
        out.append(content)
        out.append("```")
        out.append("\n</details>\n")

    report = "\n".join(out)
    Path("bench_report.md").write_text(report)
    print(f"Report written ({len(report)} bytes)")
    print(f"Languages with data: {', '.join(found) if found else 'none'}")


if __name__ == "__main__":
    main()
