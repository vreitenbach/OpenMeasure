# Resources

Additional resources for working with MeasFlow.

## Documentation

<div class="grid cards" markdown>

-   :material-file-document: **README**

    ---

    Getting started guide and project overview

    [View on GitHub →](https://github.com/vreitenbach/MeasFlow/blob/main/README.md)

-   :material-file-code: **Specification**

    ---

    Complete binary format specification

    [View SPECIFICATION.md →](https://github.com/vreitenbach/MeasFlow/blob/main/SPECIFICATION.md)

-   :material-lightbulb: **Concept**

    ---

    Design goals and architecture (German)

    [View CONCEPT.md →](https://github.com/vreitenbach/MeasFlow/blob/main/CONCEPT.md)

-   :material-license: **License**

    ---

    MIT License - free and open source

    [View LICENSE →](https://github.com/vreitenbach/MeasFlow/blob/main/LICENSE)

</div>

## Code Examples

<div class="grid cards" markdown>

-   :simple-csharp: **C# QuickStart**

    ---

    Runnable C# sample demonstrating core features

    [View sample →](https://github.com/vreitenbach/MeasFlow/tree/main/csharp/samples/QuickStart)

-   :simple-python: **Python QuickStart**

    ---

    Python example with NumPy integration

    [View sample →](https://github.com/vreitenbach/MeasFlow/blob/main/python/quickstart/quickstart.py)

-   :simple-c: **C QuickStart**

    ---

    Minimal C example using the C API

    [View sample →](https://github.com/vreitenbach/MeasFlow/blob/main/c/quickstart/quickstart.c)

-   :material-test-tube: **Test Suites**

    ---

    Comprehensive tests for all language bindings

    [View tests →](https://github.com/vreitenbach/MeasFlow/tree/main/csharp/tests)

</div>

## Package Repositories

<div class="grid cards" markdown>

-   :simple-nuget: **NuGet (C#)**

    ---

    .NET packages for C# developers

    [View package →](https://www.nuget.org/packages/MeasFlow)

-   :simple-python: **PyPI (Python)**

    ---

    Python packages via pip

    [View package →](https://pypi.org/project/measflow/)

-   :material-package-variant: **vcpkg (C)**

    ---

    C library via vcpkg (custom registry)

    [View registry →](https://github.com/vreitenbach/vcpkg-registry)

</div>

## Benchmarks

Performance comparison benchmarks are included in each language binding:

### C# Benchmarks

```bash
cd csharp/benchmarks/MeasFlow.Benchmarks
dotnet run -c Release -- --filter "*FormatComparison*"   # MeasFlow vs HDF5
dotnet run -c Release -- --filter "*CrossLanguage*"      # Cross-language
```

### Python Benchmarks

```bash
cd python
pip install h5py                              # for HDF5 comparison
python benchmarks/format_comparison.py        # MeasFlow vs HDF5
python benchmarks/cross_language.py           # Cross-language
```

### C Benchmarks

```bash
cd c
cmake -B build -DMEAS_BUILD_BENCHMARKS=ON -DCMAKE_BUILD_TYPE=Release
cmake --build build
./build/bench_format_comparison
./build/bench_cross_language
```

## Community

<div class="grid cards" markdown>

-   :material-github: **GitHub Repository**

    ---

    Source code, issues, and discussions

    [Visit repository →](https://github.com/vreitenbach/MeasFlow)

-   :material-bug: **Issue Tracker**

    ---

    Report bugs and request features

    [Open an issue →](https://github.com/vreitenbach/MeasFlow/issues)

-   :material-pull-request: **Pull Requests**

    ---

    Contribute to the project

    [View PRs →](https://github.com/vreitenbach/MeasFlow/pulls)

-   :material-message-question: **Discussions**

    ---

    Ask questions and share ideas

    [Join discussions →](https://github.com/vreitenbach/MeasFlow/discussions)

</div>

## Tools

### MeasFlow Viewer (C#)

Avalonia-based GUI for viewing measurement files:

```bash
cd csharp/tools/MeasFlow.Viewer
dotnet run
```

### Demo Generator (C#)

Generate test data for cross-language validation:

```bash
cd csharp/tools/MeasFlow.DemoGenerator
dotnet run
```

## Related Projects

### Standards and Formats

- **[ASAM MDF](https://www.asam.net/standards/detail/mdf/)** - Measurement Data Format (MDF4)
- **[HDF5](https://www.hdfgroup.org/solutions/hdf5/)** - Hierarchical Data Format
- **[TDMS](https://www.ni.com/en-us/support/documentation/supplemental/06/the-ni-tdms-file-format.html)** - National Instruments format

### AUTOSAR

- **[AUTOSAR](https://www.autosar.org/)** - Automotive Open System Architecture
- **E2E Protection** - End-to-End communication protection
- **SecOC** - Secure Onboard Communication

## Project Structure

```
MeasFlow/
├── csharp/                       # C# implementation
│   ├── src/MeasFlow/             # Core library
│   │   ├── Bus/                  # Bus data model
│   │   └── Format/               # Binary serialization
│   ├── tests/MeasFlow.Tests/     # Test suite
│   ├── samples/QuickStart/       # Sample code
│   ├── benchmarks/               # Performance tests
│   └── tools/                    # Viewer and utilities
├── python/                       # Python implementation
│   ├── measflow/                 # Package source
│   ├── tests/                    # Test suite
│   ├── quickstart/               # Sample code
│   └── benchmarks/               # Performance tests
├── c/                            # C implementation
│   ├── measflow.c/.h             # Single-file library
│   ├── tests/                    # Test suite
│   ├── quickstart/               # Sample code
│   └── benchmarks/               # Performance tests
├── docs/                         # This documentation
├── SPECIFICATION.md              # Binary format spec
├── CONCEPT.md                    # Design document
└── README.md                     # Project overview
```

## Contributing

Contributions are welcome! Please see:

- [Contributing Guidelines](https://github.com/vreitenbach/MeasFlow/blob/main/CONTRIBUTING.md) (if available)
- [Code of Conduct](https://github.com/vreitenbach/MeasFlow/blob/main/CODE_OF_CONDUCT.md) (if available)
- [Development Setup](https://github.com/vreitenbach/MeasFlow#build-from-source)

## License

MeasFlow is licensed under the [MIT License](https://github.com/vreitenbach/MeasFlow/blob/main/LICENSE).

See [Third-Party Notices](https://github.com/vreitenbach/MeasFlow/blob/main/THIRD-PARTY-NOTICES.md) for dependencies.
