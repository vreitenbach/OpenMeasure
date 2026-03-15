# TODO

## Core Format

- [x] Binary format specification (SPECIFICATION.md)
- [x] Streaming write/read support with incremental flush
- [x] Channel statistics (Welford's online algorithm)
- [x] Bus data model (CAN/CAN-FD/LIN/FlexRay/Ethernet/MOST)
- [x] AUTOSAR: PDU/ContainedPdu/Mux/E2E/SecOC
- [x] Signal decoding (Intel/Motorola byte order)
- [x] Performance benchmarks (BenchmarkDotNet)
- [x] Compression (LZ4/Zstd segments)
- [ ] Memory-mapped I/O for large files

## Cross-Language Implementations

- [x] C Reader
- [x] C Writer
- [x] Python Reader
- [x] Python Writer
- [ ] Rust Reader
- [ ] Rust Writer

## Tools & Integrations

- [ ] Comparison with other formats (TDMS, HDF5, MDF4) using same data
- [x] Data Viewer (signal plots, frame browser)
- [ ] MATLAB integration
- [ ] Excel plugin

## CI/CD

- [x] CI workflow (C#, Python, C — Linux + Windows matrix)
- [x] Release workflow (NuGet, PyPI, vcpkg registry, GitHub Release on tag push)

## Publishing

- [x] NuGet package metadata (csproj ready, `dotnet pack` works)
- [x] PyPI package metadata (pyproject.toml ready, `python -m build` works)
- [x] Vcpkg port files prepared (c/port/)
- [x] Third-party license notices (THIRD-PARTY-NOTICES.md)
- [ ] NuGet publish to nuget.org
- [ ] PyPI publish to pypi.org
- [x] Vcpkg custom registry (vreitenbach/vcpkg-registry, auto-updated on release)

