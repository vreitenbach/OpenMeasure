using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using MeasFlow;
using MeasFlow.Bus;

namespace MeasFlow.Benchmarks;

[ShortRunJob]
[MemoryDiagnoser]
public class WriteBenchmarks
{

    [Params(10_000, 100_000)]
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

    [Benchmark(Description = "MEAS Write Float32 (bulk)")]
    public long WriteFloat32Bulk()
    {
        var path = TempFile($"float32_bulk_{SampleCount}.meas");
        using var writer = MeasFile.CreateWriter(path);
        var group = writer.AddGroup("Data");
        var ch = group.AddChannel<float>("Signal");
        ch.Write(_floatData.AsSpan());
        writer.Flush();
        return new FileInfo(path).Length;
    }

    [Benchmark(Description = "MEAS Write Float64 (bulk)")]
    public long WriteFloat64Bulk()
    {
        var path = TempFile($"float64_bulk_{SampleCount}.meas");
        using var writer = MeasFile.CreateWriter(path);
        var group = writer.AddGroup("Data");
        var ch = group.AddChannel<double>("Signal");
        ch.Write(_doubleData.AsSpan());
        writer.Flush();
        return new FileInfo(path).Length;
    }

    [Benchmark(Description = "MEAS Write Float32 (per-sample)")]
    public void WriteFloat32PerSample()
    {
        var path = TempFile($"float32_ps_{SampleCount}.meas");
        using var writer = MeasFile.CreateWriter(path);
        var group = writer.AddGroup("Data");
        var ch = group.AddChannel<float>("Signal");
        for (int i = 0; i < SampleCount; i++)
            ch.Write(_floatData[i]);
    }

    [Benchmark(Description = "MEAS Write Multi-Channel (4ch Float32)")]
    public void WriteMultiChannel()
    {
        var path = TempFile($"multi_{SampleCount}.meas");
        using var writer = MeasFile.CreateWriter(path);
        var group = writer.AddGroup("Data");
        var ts = group.AddChannel<MeasTimestamp>("Time");
        var ch1 = group.AddChannel<float>("Ch1");
        var ch2 = group.AddChannel<float>("Ch2");
        var ch3 = group.AddChannel<float>("Ch3");

        var t = MeasTimestamp.Now;
        for (int i = 0; i < SampleCount; i++)
        {
            ts.Write(t + TimeSpan.FromMicroseconds(i));
            ch1.Write(_floatData[i]);
            ch2.Write(_floatData[SampleCount - 1 - i]);
            ch3.Write(_floatData[i] * 0.5f);
        }
    }

    [Benchmark(Description = "MEAS Write CAN Bus (structured frames)")]
    public void WriteCanBusFrames()
    {
        var path = TempFile($"can_{SampleCount}.meas");
        using var writer = MeasFile.CreateWriter(path);
        var can = writer.AddBusGroup("CAN1", new CanBusConfig { BaudRate = 500_000 });

        var frame = can.DefineCanFrame("EngineData", 0x100, 8);
        frame.Signals.Add(new SignalDefinition
        {
            Name = "RPM", StartBit = 0, BitLength = 16, Factor = 0.25, Unit = "rpm",
        });

        var ts = MeasTimestamp.Now;
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
