using OpenMeasure.Tests.TestData;

namespace OpenMeasure.Tests;

public class TimestampTests
{
    [Fact]
    public void FromDateTimeOffset_Roundtrip()
    {
        var dto = new DateTimeOffset(2026, 3, 13, 14, 30, 0, 500, TimeSpan.FromHours(1));
        var ts = OmxTimestamp.FromDateTimeOffset(dto);
        var result = ts.ToDateTimeOffset();

        // Nanosecond precision: DateTimeOffset has 100ns precision
        Assert.Equal(dto.UtcDateTime.Ticks, result.UtcDateTime.Ticks);
    }

    [Fact]
    public void FromSeconds_CorrectNanoseconds()
    {
        var ts = OmxTimestamp.FromSeconds(1.5);
        Assert.Equal(1_500_000_000L, ts.Nanoseconds);
    }

    [Fact]
    public void FromMilliseconds_CorrectNanoseconds()
    {
        var ts = OmxTimestamp.FromMilliseconds(1500);
        Assert.Equal(1_500_000_000L, ts.Nanoseconds);
    }

    [Fact]
    public void Epoch_IsZero()
    {
        Assert.Equal(0L, OmxTimestamp.Epoch.Nanoseconds);
        var dto = OmxTimestamp.Epoch.ToDateTimeOffset();
        Assert.Equal(1970, dto.Year);
        Assert.Equal(1, dto.Month);
        Assert.Equal(1, dto.Day);
    }

    [Fact]
    public void Subtraction_ReturnsOmxTimeSpan()
    {
        var a = OmxTimestamp.FromSeconds(10.0);
        var b = OmxTimestamp.FromSeconds(7.5);
        var diff = a - b;

        Assert.Equal(2_500_000_000L, diff.Nanoseconds);
        Assert.Equal(2.5, diff.TotalSeconds, precision: 6);
    }

    [Fact]
    public void Addition_WithTimeSpan()
    {
        var ts = OmxTimestamp.FromSeconds(10.0);
        var result = ts + TimeSpan.FromSeconds(5);
        Assert.Equal(15_000_000_000L, result.Nanoseconds);
    }

    [Fact]
    public void Comparison_Operators()
    {
        var a = OmxTimestamp.FromSeconds(1.0);
        var b = OmxTimestamp.FromSeconds(2.0);

        Assert.True(a < b);
        Assert.True(b > a);
        Assert.True(a <= b);
        Assert.True(a != b);
        Assert.False(a == b);
        Assert.True(a == OmxTimestamp.FromSeconds(1.0));
    }

    [Fact]
    public void ToString_FormatsIso8601WithNanos()
    {
        var ts = OmxTimestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 3, 13, 10, 30, 45, TimeSpan.Zero));
        var str = ts.ToString();

        Assert.StartsWith("2026-03-13T10:30:45.", str);
        Assert.EndsWith("Z", str);
    }

    [Fact]
    public void Parse_Iso8601()
    {
        var ts = OmxTimestamp.Parse("2026-03-13T10:00:00Z");
        var dto = ts.ToDateTimeOffset();
        Assert.Equal(2026, dto.Year);
        Assert.Equal(3, dto.Month);
        Assert.Equal(13, dto.Day);
        Assert.Equal(10, dto.Hour);
    }

    [Fact]
    public void Now_IsReasonable()
    {
        var before = DateTimeOffset.UtcNow;
        var ts = OmxTimestamp.Now;
        var after = DateTimeOffset.UtcNow;

        var tsDto = ts.ToDateTimeOffset();
        Assert.True(tsDto >= before.AddSeconds(-1), $"OmxTimestamp.Now ({tsDto}) is too far before system time ({before})");
        Assert.True(tsDto <= after.AddSeconds(1), $"OmxTimestamp.Now ({tsDto}) is too far after system time ({after})");
    }

    [Fact]
    public void NanosecondPrecision_PreservedInRoundtrip()
    {
        // Create timestamp with sub-microsecond precision
        long nanos = 1_710_331_200_000_123_456L; // March 13, 2024 + 123456 ns
        var ts = new OmxTimestamp(nanos);
        Assert.Equal(nanos, ts.Nanoseconds);
    }

    [Fact]
    public void GeneratedTimestamps_AreEquidistant()
    {
        var start = new DateTimeOffset(2026, 3, 13, 10, 0, 0, TimeSpan.Zero);
        var timestamps = MeasurementDataGenerator.GenerateTimestamps(start, 1000, sampleRateHz: 10_000);

        // Expected interval: 100 microseconds = 100_000 nanoseconds
        long expectedInterval = 100_000L;

        for (int i = 1; i < timestamps.Length; i++)
        {
            long interval = timestamps[i].Nanoseconds - timestamps[i - 1].Nanoseconds;
            Assert.Equal(expectedInterval, interval);
        }
    }

    [Fact]
    public void Timestamps_WriteAndRead_Roundtrip()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ts_test_{Guid.NewGuid():N}.omx");
        try
        {
            var start = new DateTimeOffset(2026, 3, 13, 10, 0, 0, TimeSpan.Zero);
            var timestamps = MeasurementDataGenerator.GenerateTimestamps(start, 10_000, 1000);

            using (var writer = OmxFile.CreateWriter(tempPath))
            {
                var group = writer.AddGroup("TimingTest");
                group.AddChannel<OmxTimestamp>("Time").Write(timestamps.AsSpan());
            }

            using var reader = OmxFile.OpenRead(tempPath);
            var readTs = reader["TimingTest"]["Time"].ReadAll<OmxTimestamp>();

            Assert.Equal(timestamps.Length, readTs.Length);
            for (int i = 0; i < timestamps.Length; i++)
                Assert.Equal(timestamps[i].Nanoseconds, readTs[i].Nanoseconds);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
