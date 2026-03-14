using MeasFlow.Tests.TestData;

namespace MeasFlow.Tests;

public class RoundtripTests : IDisposable
{
    private readonly string _tempDir;

    public RoundtripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"omx_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string TempFile(string name = "test.meas") => Path.Combine(_tempDir, name);

    [Fact]
    public void WriteAndRead_SimpleFloatChannel_RoundTrips()
    {
        var path = TempFile();
        float[] data = [1.0f, 2.5f, 3.7f, -1.2f, 0.0f];

        using (var writer = MeasFile.CreateWriter(path))
        {
            var group = writer.AddGroup("Sensors");
            var ch = group.AddChannel<float>("Temperature");
            ch.Write(data.AsSpan());
        }

        using var reader = MeasFile.OpenRead(path);
        Assert.Single(reader.Groups);
        Assert.Equal("Sensors", reader.Groups[0].Name);

        var channel = reader["Sensors"]["Temperature"];
        Assert.Equal(MeasDataType.Float32, channel.DataType);
        Assert.Equal(5, channel.SampleCount);

        var result = channel.ReadAll<float>();
        Assert.Equal(data, result);
    }

    [Fact]
    public void WriteAndRead_MultipleDataTypes_RoundTrip()
    {
        var path = TempFile();
        int[] intData = [1, -2, 3, int.MaxValue, int.MinValue];
        double[] doubleData = [Math.PI, Math.E, double.MaxValue, double.MinValue, 0.0];

        using (var writer = MeasFile.CreateWriter(path))
        {
            var group = writer.AddGroup("Mixed");
            group.AddChannel<int>("Integers").Write(intData.AsSpan());
            group.AddChannel<double>("Doubles").Write(doubleData.AsSpan());
        }

        using var reader = MeasFile.OpenRead(path);
        Assert.Equal(intData, reader["Mixed"]["Integers"].ReadAll<int>());
        Assert.Equal(doubleData, reader["Mixed"]["Doubles"].ReadAll<double>());
    }

    [Fact]
    public void WriteAndRead_Properties_RoundTrip()
    {
        var path = TempFile();

        using (var writer = MeasFile.CreateWriter(path))
        {
            var group = writer.AddGroup("Motor");
            group.Properties["Prüfstand"] = "P42";
            group.Properties["MaxRPM"] = 7000;
            group.Properties["Calibrated"] = true;

            var ch = group.AddChannel<float>("RPM");
            ch.Properties["Unit"] = "1/min";
            ch.Properties["SampleRate"] = 1000.0;
            ch.Write(1500.0f);
        }

        using var reader = MeasFile.OpenRead(path);
        var g = reader["Motor"];
        Assert.Equal("P42", g.Properties["Prüfstand"].AsString());
        Assert.Equal(7000, g.Properties["MaxRPM"].AsInt32());
        Assert.True(g.Properties["Calibrated"].AsBool());

        var rpm = g["RPM"];
        Assert.Equal("1/min", rpm.Properties["Unit"].AsString());
        Assert.Equal(1000.0, rpm.Properties["SampleRate"].AsFloat64());
    }

    [Fact]
    public void WriteAndRead_MultipleGroups()
    {
        var path = TempFile();

        using (var writer = MeasFile.CreateWriter(path))
        {
            var g1 = writer.AddGroup("Engine");
            g1.AddChannel<float>("RPM").Write(1500.0f);

            var g2 = writer.AddGroup("Transmission");
            g2.AddChannel<int>("Gear").Write(3);
        }

        using var reader = MeasFile.OpenRead(path);
        Assert.Equal(2, reader.Groups.Count);
        Assert.Equal(1500.0f, reader["Engine"]["RPM"].ReadAll<float>()[0]);
        Assert.Equal(3, reader["Transmission"]["Gear"].ReadAll<int>()[0]);
    }

    [Fact]
    public void WriteAndRead_LargeDataset_Performance()
    {
        var path = TempFile("large.meas");
        int sampleCount = 1_000_000;
        var rpm = MeasurementDataGenerator.GenerateRpmProfile(sampleCount);
        var temp = MeasurementDataGenerator.GenerateTemperature(rpm);
        var timestamps = MeasurementDataGenerator.GenerateTimestamps(
            new DateTimeOffset(2026, 3, 13, 10, 0, 0, TimeSpan.Zero),
            sampleCount, sampleRateHz: 1000);

        using (var writer = MeasFile.CreateWriter(path))
        {
            var group = writer.AddGroup("TestBench");
            group.AddChannel<MeasTimestamp>("Time").Write(timestamps.AsSpan());
            group.AddChannel<float>("RPM").Write(rpm.AsSpan());
            group.AddChannel<double>("Temperature").Write(temp.AsSpan());
        }

        var fileSize = new FileInfo(path).Length;
        Assert.True(fileSize > 0);

        using var reader = MeasFile.OpenRead(path);
        var readRpm = reader["TestBench"]["RPM"].ReadAll<float>();
        var readTime = reader["TestBench"]["Time"].ReadAll<MeasTimestamp>();

        Assert.Equal(sampleCount, readRpm.Length);
        Assert.Equal(sampleCount, readTime.Length);
        Assert.Equal(rpm[0], readRpm[0]);
        Assert.Equal(rpm[^1], readRpm[^1]);
        Assert.Equal(timestamps[0], readTime[0]);
        Assert.Equal(timestamps[^1], readTime[^1]);
    }

    [Fact]
    public void WriteAndRead_IncrementalFlush()
    {
        var path = TempFile();

        using (var writer = MeasFile.CreateWriter(path))
        {
            var group = writer.AddGroup("Streaming");
            var ch = group.AddChannel<float>("Signal");

            ch.Write(1.0f);
            ch.Write(2.0f);
            writer.Flush();

            ch.Write(3.0f);
            ch.Write(4.0f);
            // auto-flush on Dispose
        }

        using var reader = MeasFile.OpenRead(path);
        var data = reader["Streaming"]["Signal"].ReadAll<float>();
        Assert.Equal([1.0f, 2.0f, 3.0f, 4.0f], data);
    }

    [Fact]
    public void ChannelStatistics_ComputedDuringWrite_AvailableOnRead()
    {
        var path = TempFile("stats.meas");
        float[] data = [10.0f, 20.0f, 30.0f, 40.0f, 50.0f];

        using (var writer = MeasFile.CreateWriter(path))
        {
            var group = writer.AddGroup("Stats");
            var ch = group.AddChannel<float>("Values");
            ch.Write(data.AsSpan());

            // Stats available during write
            var writeStats = ch.Statistics;
            Assert.Equal(5, writeStats.Count);
            Assert.Equal(10.0, writeStats.Min, 1);
            Assert.Equal(50.0, writeStats.Max, 1);
            Assert.Equal(30.0, writeStats.Mean, 1);
            Assert.Equal(10.0, writeStats.First, 1);
            Assert.Equal(50.0, writeStats.Last, 1);
        }

        // Stats available on read without reading data
        using var reader = MeasFile.OpenRead(path);
        var channel = reader["Stats"]["Values"];
        var stats = channel.Statistics;

        Assert.NotNull(stats);
        Assert.Equal(5, stats.Value.Count);
        Assert.Equal(10.0, stats.Value.Min, 1);
        Assert.Equal(50.0, stats.Value.Max, 1);
        Assert.Equal(30.0, stats.Value.Mean, 1);
        Assert.Equal(150.0, stats.Value.Sum, 1);
        Assert.Equal(10.0, stats.Value.First, 1);
        Assert.Equal(50.0, stats.Value.Last, 1);

        // Variance of [10,20,30,40,50] = 200 (population), StdDev ≈ 14.14
        Assert.Equal(200.0, stats.Value.Variance, 1);
        Assert.Equal(14.14, stats.Value.StdDev, 1);
    }

    [Fact]
    public void ChannelStatistics_NotAvailableForBinaryChannels()
    {
        var path = TempFile("stats_bin.meas");

        using (var writer = MeasFile.CreateWriter(path))
        {
            var group = writer.AddGroup("Raw");
            var raw = group.AddRawChannel("Frames");
            raw.WriteFrame(new byte[] { 1, 2, 3 });
        }

        using var reader = MeasFile.OpenRead(path);
        Assert.Null(reader["Raw"]["Frames"].Statistics);
    }
}
