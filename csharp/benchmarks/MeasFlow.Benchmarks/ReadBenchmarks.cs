using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using MeasFlow;
using MeasFlow.Bus;

namespace MeasFlow.Benchmarks;

[ShortRunJob]
[MemoryDiagnoser]
public class ReadBenchmarks
{
    [Params(10_000, 100_000)]
    public int SampleCount { get; set; }

    private string _tempDir = null!;
    private string _floatFile = null!;
    private string _canFile = null!;
    private string _multiFile = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"omx_rbench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var rng = new Random(42);

        // Pre-create float file
        _floatFile = Path.Combine(_tempDir, "float.meas");
        using (var writer = MeasFile.CreateWriter(_floatFile))
        {
            var group = writer.AddGroup("Data");
            var ch = group.AddChannel<float>("Signal");
            for (int i = 0; i < SampleCount; i++)
                ch.Write((float)(rng.NextDouble() * 10000));
        }

        // Pre-create CAN file
        _canFile = Path.Combine(_tempDir, "can.meas");
        using (var writer = MeasFile.CreateWriter(_canFile))
        {
            var can = writer.AddBusGroup("CAN1", new CanBusConfig());
            var frame = can.DefineCanFrame("EngineData", 0x100, 8);
            frame.Signals.Add(new SignalDefinition
            {
                Name = "RPM", StartBit = 0, BitLength = 16, Factor = 0.25,
            });
            can.DefineCanFrame("BrakeData", 0x200, 8);

            var ts = MeasTimestamp.Now;
            for (int i = 0; i < SampleCount; i++)
            {
                uint id = i % 2 == 0 ? 0x100u : 0x200u;
                var payload = new byte[8];
                ushort rpmRaw = (ushort)(i % 65535);
                payload[0] = (byte)(rpmRaw & 0xFF);
                payload[1] = (byte)(rpmRaw >> 8);
                can.WriteFrame(ts, id, payload);
                ts = ts + TimeSpan.FromMilliseconds(1);
            }
        }

        // Pre-create multi-channel file
        _multiFile = Path.Combine(_tempDir, "multi.meas");
        using (var writer = MeasFile.CreateWriter(_multiFile))
        {
            var group = writer.AddGroup("Data");
            var ts = group.AddChannel<MeasTimestamp>("Time");
            var ch1 = group.AddChannel<float>("Ch1");
            var ch2 = group.AddChannel<float>("Ch2");
            var ch3 = group.AddChannel<double>("Ch3");

            var t = MeasTimestamp.Now;
            for (int i = 0; i < SampleCount; i++)
            {
                ts.Write(t + TimeSpan.FromMicroseconds(i));
                ch1.Write((float)(rng.NextDouble() * 1000));
                ch2.Write((float)(rng.NextDouble() * 1000));
                ch3.Write(rng.NextDouble() * 1000);
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Benchmark(Description = "MEAS Read Float32 (ReadAll)")]
    public float[] ReadFloat32()
    {
        using var reader = MeasFile.OpenRead(_floatFile);
        return reader["Data"]["Signal"].ReadAll<float>();
    }

    [Benchmark(Description = "MEAS Read Multi-Channel (4ch)")]
    public int ReadMultiChannel()
    {
        using var reader = MeasFile.OpenRead(_multiFile);
        var ts = reader["Data"]["Time"].ReadAll<MeasTimestamp>();
        var ch1 = reader["Data"]["Ch1"].ReadAll<float>();
        var ch2 = reader["Data"]["Ch2"].ReadAll<float>();
        var ch3 = reader["Data"]["Ch3"].ReadAll<double>();
        return ts.Length + ch1.Length + ch2.Length + ch3.Length;
    }

    [Benchmark(Description = "MEAS Read Statistics Only (no data)")]
    public ChannelStatistics ReadStatisticsOnly()
    {
        using var reader = MeasFile.OpenRead(_floatFile);
        return reader["Data"]["Signal"].Statistics!.Value;
    }

    [Benchmark(Description = "MEAS Decode CAN Signal from Raw")]
    public double[] DecodeCanSignal()
    {
        using var reader = MeasFile.OpenRead(_canFile);
        return reader["CAN1"].DecodeSignal("RPM");
    }

    [Benchmark(Description = "MEAS Read CAN Raw Frames")]
    public int ReadCanRawFrames()
    {
        using var reader = MeasFile.OpenRead(_canFile);
        return reader["CAN1"]["RawFrames"].ReadFrames().Count;
    }
}
