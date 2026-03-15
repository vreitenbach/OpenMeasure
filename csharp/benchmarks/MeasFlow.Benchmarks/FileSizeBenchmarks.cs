using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using MeasFlow;
using MeasFlow.Bus;

namespace MeasFlow.Benchmarks;

/// <summary>
/// Measures file sizes for different data patterns to evaluate storage efficiency.
/// </summary>
[ShortRunJob]
public class FileSizeBenchmarks
{
    private string _tempDir = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"omx_size_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Benchmark(Description = "File size: 1M Float32 samples")]
    public long FileSize_1M_Float32()
    {
        var path = Path.Combine(_tempDir, "1m_f32.meas");
        using (var writer = MeasFile.CreateWriter(path))
        {
            var group = writer.AddGroup("Data");
            var ch = group.AddChannel<float>("Signal");
            var rng = new Random(42);
            for (int i = 0; i < 1_000_000; i++)
                ch.Write((float)(rng.NextDouble() * 10000));
        }
        return new FileInfo(path).Length;
    }

    [Benchmark(Description = "File size: 1M Float64 samples")]
    public long FileSize_1M_Float64()
    {
        var path = Path.Combine(_tempDir, "1m_f64.meas");
        using (var writer = MeasFile.CreateWriter(path))
        {
            var group = writer.AddGroup("Data");
            var ch = group.AddChannel<double>("Signal");
            var rng = new Random(42);
            for (int i = 0; i < 1_000_000; i++)
                ch.Write(rng.NextDouble() * 10000);
        }
        return new FileInfo(path).Length;
    }

    [Benchmark(Description = "File size: 100K CAN frames")]
    public long FileSize_100K_CanFrames()
    {
        var path = Path.Combine(_tempDir, "100k_can.meas");
        using (var writer = MeasFile.CreateWriter(path))
        {
            var can = writer.AddBusGroup("CAN1", new CanBusConfig());
            can.DefineCanFrame("Msg1", 0x100, 8);
            can.DefineCanFrame("Msg2", 0x200, 8);

            var ts = MeasTimestamp.Now;
            for (int i = 0; i < 100_000; i++)
            {
                can.WriteFrame(ts, (uint)(i % 2 == 0 ? 0x100 : 0x200), new byte[8]);
                ts = ts + TimeSpan.FromMilliseconds(1);
            }
        }
        return new FileInfo(path).Length;
    }

    [Benchmark(Description = "File size: 10ch x 100K samples")]
    public long FileSize_10ch_100K()
    {
        var path = Path.Combine(_tempDir, "10ch.meas");
        using (var writer = MeasFile.CreateWriter(path))
        {
            var group = writer.AddGroup("Data");
            var channels = new ChannelWriter<float>[10];
            for (int c = 0; c < 10; c++)
                channels[c] = group.AddChannel<float>($"Ch{c}");

            var rng = new Random(42);
            for (int i = 0; i < 100_000; i++)
                for (int c = 0; c < 10; c++)
                    channels[c].Write((float)(rng.NextDouble() * 1000));
        }
        return new FileInfo(path).Length;
    }

    /// <summary>
    /// Theoretical minimum: raw data bytes only, no overhead.
    /// Useful as baseline for efficiency comparison.
    /// </summary>
    [Benchmark(Description = "Baseline: raw 1M x 4B write")]
    public long Baseline_RawBinaryWrite()
    {
        var path = Path.Combine(_tempDir, "raw.bin");
        using var fs = File.Create(path);
        var rng = new Random(42);
        Span<byte> buf = stackalloc byte[4];
        for (int i = 0; i < 1_000_000; i++)
        {
            BitConverter.TryWriteBytes(buf, (float)(rng.NextDouble() * 10000));
            fs.Write(buf);
        }
        return new FileInfo(path).Length;
    }
}
