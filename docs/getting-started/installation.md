# Installation

MeasFlow provides native bindings for C#, Python, and C. Choose the installation method for your language.

## C# / .NET

MeasFlow is available on NuGet and requires .NET 10 or later.

```sh
dotnet add package MeasFlow
```

[View on NuGet →](https://www.nuget.org/packages/MeasFlow){ .md-button }

## Python

MeasFlow is available on PyPI and requires Python 3.10 or later.

```sh
pip install measflow
```

[View on PyPI →](https://pypi.org/project/measflow/){ .md-button }

## C

MeasFlow is available via vcpkg (custom registry until official publication).

To use measflow via vcpkg, add a `vcpkg-configuration.json` to your project:

```json
{
  "default-registry": null,
  "registries": [
    {
      "kind": "git",
      "repository": "https://github.com/vreitenbach/vcpkg-registry",
      "baseline": "d6473e3037973c8a5d465ef3fc1955b8e4f58557",
      "packages": ["measflow"]
    }
  ]
}
```

Then install:

```sh
vcpkg install measflow
```

[View vcpkg registry →](https://github.com/vreitenbach/vcpkg-registry){ .md-button }

## Build from Source

### Prerequisites

- **C#**: .NET 10 SDK
- **Python**: Python 3.10+ with pip
- **C**: C99 compiler, CMake 3.15+

### Clone Repository

```sh
git clone https://github.com/vreitenbach/MeasFlow.git
cd MeasFlow
```

### Build C#

```sh
cd csharp
dotnet build
dotnet test
```

### Build Python

```sh
cd python
pip install -e .
pytest tests/
```

### Build C

```sh
cd c

# With compression (requires lz4 + zstd via vcpkg):
cmake -B build -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_TOOLCHAIN_FILE=$VCPKG_INSTALLATION_ROOT/scripts/buildsystems/vcpkg.cmake
cmake --build build

# Or without compression:
cmake -B build -DCMAKE_BUILD_TYPE=Release \
  -DMEAS_WITH_LZ4=OFF -DMEAS_WITH_ZSTD=OFF
cmake --build build
```

!!! note
    `CMAKE_BUILD_TYPE` is for single-config generators (Ninja, Make).
    For multi-config generators (Visual Studio, Xcode), add `--config Release` to build
    commands and find binaries under `<build-dir>/Release/`.

## Next Steps

- [Quick Start Guide](quickstart.md) - Get started with your first measurement file
- [API Reference](../api/csharp.md) - Detailed API documentation
- [Examples](https://github.com/vreitenbach/MeasFlow/tree/main/csharp/samples) - Sample code
