# TODO

## Core Format

- [x] Binary format specification (SPECIFICATION.md)
- [x] Streaming write/read support with incremental flush
- [x] Channel statistics (Welford's online algorithm)
- [x] Bus data model (CAN/CAN-FD/LIN/FlexRay/Ethernet/MOST)
- [x] AUTOSAR: PDU/ContainedPdu/Mux/E2E/SecOC
- [x] Signal decoding (Intel/Motorola byte order)
- [x] Performance benchmarks (BenchmarkDotNet)
- [ ] Compression (LZ4/Zstd segments)
- [ ] Memory-mapped I/O for large files

## Cross-Language Implementations

- [ ] C Reader
- [ ] C Writer
- [x] Python Reader
- [x] Python Writer
- [ ] Rust Reader
- [ ] Rust Writer

## Tools & Integrations

- [ ] Comparison with other formats (TDMS, HDF5, MDF4) using same data
- [x] Data Viewer (signal plots, frame browser)
- [ ] MATLAB integration
- [ ] Excel plugin

## Publishing

- [ ] NuGet package release for C#
- [ ] PyPI release for Python
- [ ] Vcpkg release for C
