using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using MeasFlow;
using PureHDF;

namespace MeasFlow.Benchmarks;

/// <summary>
/// Head-to-head comparison: MeasFlow vs HDF5 (PureHDF).
///
/// TDMS is excluded: no open-source .NET library supports writing.
/// MDF4 is excluded: no open-source .NET library exists at all.
/// This is itself a key argument for MeasFlow — open formats need open tooling.
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class FormatComparisonBenchmarks
{
    [Params(100_000, 1_000_000)]
    public int SampleCount { get; set; }

    private string _tempDir = null!;
    private float[] _data = null!;
    private string _measFile = null!;
    private string _hdf5File = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fmt_cmp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var rng = new Random(42);
        _data = new float[SampleCount];
        for (int i = 0; i < SampleCount; i++)
            _data[i] = (float)(rng.NextDouble() * 10000);

        // Pre-create files for read benchmarks
        _measFile = Path.Combine(_tempDir, "read.meas");
        WriteMeasFlowFile(_measFile, _data);

        _hdf5File = Path.Combine(_tempDir, "read.h5");
        WriteHdf5File(_hdf5File, _data);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    // ── Write: Single Channel ────────────────────────────────────────────

    [BenchmarkCategory("Write 1ch"), Benchmark(Description = "MeasFlow")]
    public long Write_MeasFlow_1ch()
    {
        var path = Path.Combine(_tempDir, $"w1_{SampleCount}.meas");
        WriteMeasFlowFile(path, _data);
        return new FileInfo(path).Length;
    }

    [BenchmarkCategory("Write 1ch"), Benchmark(Description = "HDF5 (PureHDF)")]
    public long Write_HDF5_1ch()
    {
        var path = Path.Combine(_tempDir, $"w1_{SampleCount}.h5");
        WriteHdf5File(path, _data);
        return new FileInfo(path).Length;
    }

    // ── Write: 10 Channels ───────────────────────────────────────────────

    [BenchmarkCategory("Write 10ch"), Benchmark(Description = "MeasFlow")]
    public long Write_MeasFlow_10ch()
    {
        var path = Path.Combine(_tempDir, $"w10_{SampleCount}.meas");
        using var writer = MeasFile.CreateWriter(path);
        var group = writer.AddGroup("Data");
        var channels = new ChannelWriter<float>[10];
        for (int c = 0; c < 10; c++)
            channels[c] = group.AddChannel<float>($"Ch{c}", trackStatistics: false);

        for (int i = 0; i < SampleCount; i++)
            for (int c = 0; c < 10; c++)
                channels[c].Write(_data[i]);

        return new FileInfo(path).Length;
    }

    [BenchmarkCategory("Write 10ch"), Benchmark(Description = "HDF5 (PureHDF)")]
    public long Write_HDF5_10ch()
    {
        var path = Path.Combine(_tempDir, $"w10_{SampleCount}.h5");
        var group = new H5Group();
        for (int c = 0; c < 10; c++)
            group[$"Ch{c}"] = _data;

        new H5File { ["Data"] = group }.Write(path);
        return new FileInfo(path).Length;
    }

    // ── Read: Single Channel ─────────────────────────────────────────────

    [BenchmarkCategory("Read 1ch"), Benchmark(Description = "MeasFlow")]
    public float[] Read_MeasFlow_1ch()
    {
        using var reader = MeasFile.OpenRead(_measFile);
        return reader["Data"]["Signal"].ReadAll<float>();
    }

    [BenchmarkCategory("Read 1ch"), Benchmark(Description = "HDF5 (PureHDF)")]
    public float[] Read_HDF5_1ch()
    {
        using var file = H5File.OpenRead(_hdf5File);
        var dataset = file.Dataset("/Data/Signal");
        return dataset.Read<float[]>();
    }

    // ── Streaming Write (incremental flush) ──────────────────────────────

    [BenchmarkCategory("Streaming Write"), Benchmark(Description = "MeasFlow (10 flushes)")]
    public long StreamingWrite_MeasFlow()
    {
        var path = Path.Combine(_tempDir, $"stream_{SampleCount}.meas");
        using var writer = MeasFile.CreateWriter(path);
        var group = writer.AddGroup("Data");
        var ch = group.AddChannel<float>("Signal", trackStatistics: false);

        int chunkSize = SampleCount / 10;
        for (int chunk = 0; chunk < 10; chunk++)
        {
            ch.Write(_data.AsSpan(chunk * chunkSize, chunkSize));
            writer.Flush();
        }

        return new FileInfo(path).Length;
    }

    [BenchmarkCategory("Streaming Write"), Benchmark(Description = "HDF5 — no streaming support")]
    public long StreamingWrite_HDF5()
    {
        // HDF5 does not support incremental writes to a file.
        // The entire dataset must be known upfront or use extensible datasets
        // which require the native HDF5 library (not available in PureHDF).
        // This benchmark writes the full dataset at once for comparison.
        var path = Path.Combine(_tempDir, $"stream_{SampleCount}.h5");
        new H5File
        {
            ["Data"] = new H5Group { ["Signal"] = _data }
        }.Write(path);
        return new FileInfo(path).Length;
    }

    // ── File Size ────────────────────────────────────────────────────────

    [BenchmarkCategory("File Size"), Benchmark(Description = "MeasFlow")]
    public long FileSize_MeasFlow() => Write_MeasFlow_1ch();

    [BenchmarkCategory("File Size"), Benchmark(Description = "HDF5 (PureHDF)")]
    public long FileSize_HDF5()
    {
        var path = Path.Combine(_tempDir, $"size_{SampleCount}.h5");
        WriteHdf5File(path, _data);
        return new FileInfo(path).Length;
    }

    [BenchmarkCategory("File Size"), Benchmark(Description = "Raw binary (theoretical min)")]
    public long FileSize_Raw()
    {
        var path = Path.Combine(_tempDir, $"size_{SampleCount}.bin");
        using var fs = File.Create(path);
        fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_data.AsSpan()));
        return new FileInfo(path).Length;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void WriteMeasFlowFile(string path, float[] data)
    {
        using var writer = MeasFile.CreateWriter(path);
        var group = writer.AddGroup("Data");
        // Disable stats for fair comparison — PureHDF doesn't compute them either
        var ch = group.AddChannel<float>("Signal", trackStatistics: false);
        ch.Write(data.AsSpan());
    }

    private static void WriteHdf5File(string path, float[] data)
    {
        new H5File
        {
            ["Data"] = new H5Group { ["Signal"] = data }
        }.Write(path);
    }
}
