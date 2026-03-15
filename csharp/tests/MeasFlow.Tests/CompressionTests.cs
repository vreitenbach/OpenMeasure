using MeasFlow.Tests.TestData;

namespace MeasFlow.Tests;

public class CompressionTests : IDisposable
{
    private readonly string _tempDir;

    public CompressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"meas_comp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string TempFile(string name = "test.meas") => Path.Combine(_tempDir, name);

    [Theory]
    [InlineData(MeasCompression.Lz4)]
    [InlineData(MeasCompression.Zstd)]
    public void Roundtrip_FloatChannel_WithCompression(MeasCompression compression)
    {
        var path = TempFile($"float_{compression}.meas");
        float[] data = [1.0f, 2.5f, 3.7f, -1.2f, 0.0f, 100.0f, -999.9f];

        using (var writer = MeasFile.CreateWriter(path, compression))
        {
            var group = writer.AddGroup("Sensors");
            group.AddChannel<float>("Temperature").Write(data.AsSpan());
        }

        using var reader = MeasFile.OpenRead(path);
        var result = reader["Sensors"]["Temperature"].ReadAll<float>();
        Assert.Equal(data, result);
    }

    [Theory]
    [InlineData(MeasCompression.Lz4)]
    [InlineData(MeasCompression.Zstd)]
    public void Roundtrip_MultipleDataTypes_WithCompression(MeasCompression compression)
    {
        var path = TempFile($"multi_{compression}.meas");
        int[] ints = [1, -2, 3, int.MaxValue, int.MinValue];
        double[] doubles = [Math.PI, Math.E, double.MaxValue, double.MinValue, 0.0];
        long[] longs = [long.MinValue, 0, long.MaxValue];

        using (var writer = MeasFile.CreateWriter(path, compression))
        {
            var group = writer.AddGroup("Mixed");
            group.AddChannel<int>("Integers").Write(ints.AsSpan());
            group.AddChannel<double>("Doubles").Write(doubles.AsSpan());
            group.AddChannel<long>("Longs").Write(longs.AsSpan());
        }

        using var reader = MeasFile.OpenRead(path);
        Assert.Equal(ints, reader["Mixed"]["Integers"].ReadAll<int>());
        Assert.Equal(doubles, reader["Mixed"]["Doubles"].ReadAll<double>());
        Assert.Equal(longs, reader["Mixed"]["Longs"].ReadAll<long>());
    }

    [Theory]
    [InlineData(MeasCompression.Lz4)]
    [InlineData(MeasCompression.Zstd)]
    public void Roundtrip_LargeDataset_WithCompression(MeasCompression compression)
    {
        var path = TempFile($"large_{compression}.meas");
        int sampleCount = 100_000;
        var rpm = MeasurementDataGenerator.GenerateRpmProfile(sampleCount);
        var temp = MeasurementDataGenerator.GenerateTemperature(rpm);

        using (var writer = MeasFile.CreateWriter(path, compression))
        {
            var group = writer.AddGroup("TestBench");
            group.AddChannel<float>("RPM").Write(rpm.AsSpan());
            group.AddChannel<double>("Temperature").Write(temp.AsSpan());
        }

        using var reader = MeasFile.OpenRead(path);
        var readRpm = reader["TestBench"]["RPM"].ReadAll<float>();
        var readTemp = reader["TestBench"]["Temperature"].ReadAll<double>();
        Assert.Equal(sampleCount, readRpm.Length);
        Assert.Equal(sampleCount, readTemp.Length);
        Assert.Equal(rpm, readRpm);
        Assert.Equal(temp, readTemp);
    }

    [Theory]
    [InlineData(MeasCompression.Lz4)]
    [InlineData(MeasCompression.Zstd)]
    public void Compressed_SmallerThanUncompressed_LargeRepetitiveData(MeasCompression compression)
    {
        var pathNone = TempFile($"none_{compression}.meas");
        var pathComp = TempFile($"comp_{compression}.meas");
        int sampleCount = 50_000;

        // Repetitive data compresses well
        var data = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
            data[i] = (float)(i % 100);

        using (var writer = MeasFile.CreateWriter(pathNone))
        {
            writer.AddGroup("G").AddChannel<float>("Ch").Write(data.AsSpan());
        }

        using (var writer = MeasFile.CreateWriter(pathComp, compression))
        {
            writer.AddGroup("G").AddChannel<float>("Ch").Write(data.AsSpan());
        }

        long sizeNone = new FileInfo(pathNone).Length;
        long sizeComp = new FileInfo(pathComp).Length;
        Assert.True(sizeComp < sizeNone,
            $"Compressed ({sizeComp:N0} bytes) should be smaller than uncompressed ({sizeNone:N0} bytes)");
    }

    [Theory]
    [InlineData(MeasCompression.Lz4)]
    [InlineData(MeasCompression.Zstd)]
    public void Roundtrip_IncrementalFlush_WithCompression(MeasCompression compression)
    {
        var path = TempFile($"flush_{compression}.meas");

        using (var writer = MeasFile.CreateWriter(path, compression))
        {
            var group = writer.AddGroup("Streaming");
            var ch = group.AddChannel<float>("Signal");

            ch.Write(1.0f);
            ch.Write(2.0f);
            writer.Flush();

            ch.Write(3.0f);
            ch.Write(4.0f);
            writer.Flush();

            ch.Write(5.0f);
        }

        using var reader = MeasFile.OpenRead(path);
        var data = reader["Streaming"]["Signal"].ReadAll<float>();
        Assert.Equal([1.0f, 2.0f, 3.0f, 4.0f, 5.0f], data);
    }

    [Theory]
    [InlineData(MeasCompression.Lz4)]
    [InlineData(MeasCompression.Zstd)]
    public void Roundtrip_RawFrames_WithCompression(MeasCompression compression)
    {
        var path = TempFile($"raw_{compression}.meas");
        byte[][] frames = [[1, 2, 3, 4], [0xDE, 0xAD, 0xBE, 0xEF], [0xFF]];

        using (var writer = MeasFile.CreateWriter(path, compression))
        {
            var group = writer.AddGroup("Bus");
            var raw = group.AddRawChannel("Frames");
            foreach (var f in frames) raw.WriteFrame(f);
        }

        using var reader = MeasFile.OpenRead(path);
        var readFrames = reader["Bus"]["Frames"].ReadFrames();
        Assert.Equal(frames.Length, readFrames.Count);
        for (int i = 0; i < frames.Length; i++)
            Assert.Equal(frames[i], readFrames[i]);
    }

    [Theory]
    [InlineData(MeasCompression.Lz4)]
    [InlineData(MeasCompression.Zstd)]
    public void Statistics_PreservedWithCompression(MeasCompression compression)
    {
        var path = TempFile($"stats_{compression}.meas");
        float[] data = [10.0f, 20.0f, 30.0f, 40.0f, 50.0f];

        using (var writer = MeasFile.CreateWriter(path, compression))
        {
            var group = writer.AddGroup("Stats");
            group.AddChannel<float>("Values").Write(data.AsSpan());
        }

        using var reader = MeasFile.OpenRead(path);
        var stats = reader["Stats"]["Values"].Statistics;
        Assert.NotNull(stats);
        Assert.Equal(5, stats.Value.Count);
        Assert.Equal(10.0, stats.Value.Min, 1);
        Assert.Equal(50.0, stats.Value.Max, 1);
        Assert.Equal(30.0, stats.Value.Mean, 1);
    }

    [Fact]
    public void Uncompressed_ReaderHandles_FlagZero()
    {
        var path = TempFile("uncompressed.meas");
        float[] data = [1.0f, 2.0f, 3.0f];

        using (var writer = MeasFile.CreateWriter(path))
        {
            writer.AddGroup("G").AddChannel<float>("Ch").Write(data.AsSpan());
        }

        using var reader = MeasFile.OpenRead(path);
        Assert.Equal(data, reader["G"]["Ch"].ReadAll<float>());
    }
}
