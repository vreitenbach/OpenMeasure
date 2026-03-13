using OpenMeasure.Tests.TestData;

namespace OpenMeasure.Tests;

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

    private string TempFile(string name = "test.omx") => Path.Combine(_tempDir, name);

    [Fact]
    public void WriteAndRead_SimpleFloatChannel_RoundTrips()
    {
        var path = TempFile();
        float[] data = [1.0f, 2.5f, 3.7f, -1.2f, 0.0f];

        using (var writer = OmxFile.CreateWriter(path))
        {
            var group = writer.AddGroup("Sensors");
            var ch = group.AddChannel<float>("Temperature");
            ch.Write(data.AsSpan());
        }

        using var reader = OmxFile.OpenRead(path);
        Assert.Single(reader.Groups);
        Assert.Equal("Sensors", reader.Groups[0].Name);

        var channel = reader["Sensors"]["Temperature"];
        Assert.Equal(OmxDataType.Float32, channel.DataType);
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

        using (var writer = OmxFile.CreateWriter(path))
        {
            var group = writer.AddGroup("Mixed");
            group.AddChannel<int>("Integers").Write(intData.AsSpan());
            group.AddChannel<double>("Doubles").Write(doubleData.AsSpan());
        }

        using var reader = OmxFile.OpenRead(path);
        Assert.Equal(intData, reader["Mixed"]["Integers"].ReadAll<int>());
        Assert.Equal(doubleData, reader["Mixed"]["Doubles"].ReadAll<double>());
    }

    [Fact]
    public void WriteAndRead_Properties_RoundTrip()
    {
        var path = TempFile();

        using (var writer = OmxFile.CreateWriter(path))
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

        using var reader = OmxFile.OpenRead(path);
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

        using (var writer = OmxFile.CreateWriter(path))
        {
            var g1 = writer.AddGroup("Engine");
            g1.AddChannel<float>("RPM").Write(1500.0f);

            var g2 = writer.AddGroup("Transmission");
            g2.AddChannel<int>("Gear").Write(3);
        }

        using var reader = OmxFile.OpenRead(path);
        Assert.Equal(2, reader.Groups.Count);
        Assert.Equal(1500.0f, reader["Engine"]["RPM"].ReadAll<float>()[0]);
        Assert.Equal(3, reader["Transmission"]["Gear"].ReadAll<int>()[0]);
    }

    [Fact]
    public void WriteAndRead_LargeDataset_Performance()
    {
        var path = TempFile("large.omx");
        int sampleCount = 1_000_000;
        var rpm = MeasurementDataGenerator.GenerateRpmProfile(sampleCount);
        var temp = MeasurementDataGenerator.GenerateTemperature(rpm);
        var timestamps = MeasurementDataGenerator.GenerateTimestamps(
            new DateTimeOffset(2026, 3, 13, 10, 0, 0, TimeSpan.Zero),
            sampleCount, sampleRateHz: 1000);

        using (var writer = OmxFile.CreateWriter(path))
        {
            var group = writer.AddGroup("TestBench");
            group.AddChannel<OmxTimestamp>("Time").Write(timestamps.AsSpan());
            group.AddChannel<float>("RPM").Write(rpm.AsSpan());
            group.AddChannel<double>("Temperature").Write(temp.AsSpan());
        }

        var fileSize = new FileInfo(path).Length;
        Assert.True(fileSize > 0);

        using var reader = OmxFile.OpenRead(path);
        var readRpm = reader["TestBench"]["RPM"].ReadAll<float>();
        var readTime = reader["TestBench"]["Time"].ReadAll<OmxTimestamp>();

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

        using (var writer = OmxFile.CreateWriter(path))
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

        using var reader = OmxFile.OpenRead(path);
        var data = reader["Streaming"]["Signal"].ReadAll<float>();
        Assert.Equal([1.0f, 2.0f, 3.0f, 4.0f], data);
    }
}
