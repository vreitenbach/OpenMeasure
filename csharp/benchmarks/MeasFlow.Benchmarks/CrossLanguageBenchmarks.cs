using BenchmarkDotNet.Attributes;
using MeasFlow;

namespace MeasFlow.Benchmarks;

/// <summary>
/// Cross-language MeasFlow benchmarks (C# binding).
/// Measures write, streaming write, and read performance for direct comparison
/// with the Python and C benchmark suites.
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class CrossLanguageBenchmarks
{
    [Params(100_000, 1_000_000)]
    public int SampleCount { get; set; }

    private string _tempDir = null!;
    private float[] _data = null!;
    private string _readFile = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"xlang_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var rng = new Random(42);
        _data = new float[SampleCount];
        for (int i = 0; i < SampleCount; i++)
            _data[i] = (float)(rng.NextDouble() * 10000);

        _readFile = Path.Combine(_tempDir, "read.meas");
        using var writer = MeasFile.CreateWriter(_readFile);
        var group = writer.AddGroup("Data");
        var ch = group.AddChannel<float>("Signal");
        ch.Write(_data.AsSpan());
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Benchmark(Description = "Write")]
    public void Write()
    {
        var path = Path.Combine(_tempDir, "write.meas");
        using var writer = MeasFile.CreateWriter(path);
        var group = writer.AddGroup("Data");
        var ch = group.AddChannel<float>("Signal");
        ch.Write(_data.AsSpan());
    }

    [Benchmark(Description = "Streaming Write (10 flushes)")]
    public void StreamingWrite()
    {
        var path = Path.Combine(_tempDir, "stream.meas");
        using var writer = MeasFile.CreateWriter(path);
        var group = writer.AddGroup("Data");
        var ch = group.AddChannel<float>("Signal");

        int chunkSize = SampleCount / 10;
        for (int i = 0; i < 10; i++)
        {
            ch.Write(_data.AsSpan(i * chunkSize, chunkSize));
            writer.Flush();
        }
    }

    [Benchmark(Description = "Read")]
    public float[] Read()
    {
        using var reader = MeasFile.OpenRead(_readFile);
        return reader["Data"]["Signal"].ReadAll<float>();
    }
}
