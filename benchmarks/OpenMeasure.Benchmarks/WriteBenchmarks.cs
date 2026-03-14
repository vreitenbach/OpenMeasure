using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using OpenMeasure;
using OpenMeasure.Bus;

namespace OpenMeasure.Benchmarks;

[Config(typeof(Config))]
[MemoryDiagnoser]
public class WriteBenchmarks
{
    private class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.ShortRun.WithWarmupCount(3).WithIterationCount(5));
            AddColumn(new ThroughputColumn());
        }
    }

    [Params(10_000, 100_000, 1_000_000)]
    public int SampleCount { get; set; }

    private string _tempDir = null!;
    private float[] _floatData = null!;
    private double[] _doubleData = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"omx_bench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var rng = new Random(42);
        _floatData = new float[SampleCount];
        _doubleData = new double[SampleCount];
        for (int i = 0; i < SampleCount; i++)
        {
            _floatData[i] = (float)(rng.NextDouble() * 10000);
            _doubleData[i] = rng.NextDouble() * 10000;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string TempFile(string name) => Path.Combine(_tempDir, name);

    [Benchmark(Description = "OMX Write Float32 (bulk)")]
    public long WriteFloat32Bulk()
    {
        var path = TempFile($"float32_bulk_{SampleCount}.omx");
        using var writer = OmxFile.CreateWriter(path);
        var group = writer.AddGroup("Data");
        var ch = group.AddChannel<float>("Signal");
        ch.Write(_floatData.AsSpan());
        writer.Flush();
        return new FileInfo(path).Length;
    }

    [Benchmark(Description = "OMX Write Float64 (bulk)")]
    public long WriteFloat64Bulk()
    {
        var path = TempFile($"float64_bulk_{SampleCount}.omx");
        using var writer = OmxFile.CreateWriter(path);
        var group = writer.AddGroup("Data");
        var ch = group.AddChannel<double>("Signal");
        ch.Write(_doubleData.AsSpan());
        writer.Flush();
        return new FileInfo(path).Length;
    }

    [Benchmark(Description = "OMX Write Float32 (per-sample)")]
    public void WriteFloat32PerSample()
    {
        var path = TempFile($"float32_ps_{SampleCount}.omx");
        using var writer = OmxFile.CreateWriter(path);
        var group = writer.AddGroup("Data");
        var ch = group.AddChannel<float>("Signal");
        for (int i = 0; i < SampleCount; i++)
            ch.Write(_floatData[i]);
    }

    [Benchmark(Description = "OMX Write Multi-Channel (4ch Float32)")]
    public void WriteMultiChannel()
    {
        var path = TempFile($"multi_{SampleCount}.omx");
        using var writer = OmxFile.CreateWriter(path);
        var group = writer.AddGroup("Data");
        var ts = group.AddChannel<OmxTimestamp>("Time");
        var ch1 = group.AddChannel<float>("Ch1");
        var ch2 = group.AddChannel<float>("Ch2");
        var ch3 = group.AddChannel<float>("Ch3");

        var t = OmxTimestamp.Now;
        for (int i = 0; i < SampleCount; i++)
        {
            ts.Write(t + TimeSpan.FromMicroseconds(i));
            ch1.Write(_floatData[i]);
            ch2.Write(_floatData[SampleCount - 1 - i]);
            ch3.Write(_floatData[i] * 0.5f);
        }
    }

    [Benchmark(Description = "OMX Write CAN Bus (structured frames)")]
    public void WriteCanBusFrames()
    {
        var path = TempFile($"can_{SampleCount}.omx");
        using var writer = OmxFile.CreateWriter(path);
        var can = writer.AddBusGroup("CAN1", new CanBusConfig { BaudRate = 500_000 });

        var frame = can.DefineCanFrame("EngineData", 0x100, 8);
        frame.Signals.Add(new SignalDefinition
        {
            Name = "RPM", StartBit = 0, BitLength = 16, Factor = 0.25, Unit = "rpm",
        });

        var ts = OmxTimestamp.Now;
        Span<byte> payload = stackalloc byte[8];

        for (int i = 0; i < SampleCount; i++)
        {
            ushort rpmRaw = (ushort)(i % 65535);
            payload[0] = (byte)(rpmRaw & 0xFF);
            payload[1] = (byte)(rpmRaw >> 8);
            can.WriteFrame(ts, 0x100, payload);
            ts = ts + TimeSpan.FromMilliseconds(1);
        }
    }
}

/// <summary>Custom column showing throughput in MB/s.</summary>
public class ThroughputColumn : IColumn
{
    public string Id => "Throughput";
    public string ColumnName => "Throughput";
    public bool AlwaysShow => false;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => "Estimated throughput in MB/s";

    public string GetValue(BenchmarkDotNet.Reports.Summary summary, BenchmarkDotNet.Running.BenchmarkCase benchmarkCase)
    {
        var report = summary[benchmarkCase];
        if (report?.ResultStatistics == null) return "N/A";

        var sampleCount = (int)(benchmarkCase.Parameters["SampleCount"] ?? 0);
        if (sampleCount == 0) return "N/A";

        // Estimate bytes: 4 bytes per float32 sample (approximate)
        double bytesWritten = sampleCount * 4.0;
        double nsPerOp = report.ResultStatistics.Mean;
        double mbPerSec = bytesWritten / nsPerOp * 1000.0; // ns→s, bytes→MB

        return $"{mbPerSec:F1} MB/s";
    }

    public string GetValue(BenchmarkDotNet.Reports.Summary summary, BenchmarkDotNet.Running.BenchmarkCase benchmarkCase, BenchmarkDotNet.Reports.SummaryStyle style)
        => GetValue(summary, benchmarkCase);

    public bool IsDefault(BenchmarkDotNet.Reports.Summary summary, BenchmarkDotNet.Running.BenchmarkCase benchmarkCase) => false;
    public bool IsAvailable(BenchmarkDotNet.Reports.Summary summary) => true;
}
