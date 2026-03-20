using MeasFlow.Bus;

namespace MeasFlow.Tests;

/// <summary>
/// Cross-language roundtrip tests.
/// Verifies files written by C, C#, and Python produce identical results.
/// </summary>
public class CrossLanguageTests : IDisposable
{
    private readonly string _tempDir;

    // ── Reference values (must match across all three languages) ────────────

    // Group "Analog": 3 channels × 10,000 samples, flushed every 1,000
    const int SampleCount = 10_000;
    const int FlushInterval = 1_000;

    // Deterministic float32 data: RPM[i] = 1000.0f + i * 0.5f
    static float ExpectedRpm(int i) => 1000.0f + i * 0.5f;
    // Deterministic int32 data: Counter[i] = i * 3 - 5000
    static int ExpectedCounter(int i) => i * 3 - 5000;
    // Deterministic timestamp: base + i microseconds
    static readonly long BaseTimestampNs = 1_700_000_000_000_000_000L; // 2023-11-14T22:13:20Z
    static long ExpectedTimestampNs(int i) => BaseTimestampNs + (long)i * 1_000;

    // Expected statistics for RPM channel
    static readonly float ExpectedRpmFirst = 1000.0f;
    static readonly float ExpectedRpmLast = 1000.0f + 9999 * 0.5f;

    // Group "CAN_Test": bus group with known frames
    const uint EngineFrameId = 0x100;
    const int CanFrameCount = 100;

    // EngineRPM signal: startBit=0, bitLen=16, factor=0.25, offset=0
    // Raw value for frame i: (uint16)((3000 + i*10) / 0.25) = (3000+i*10)*4
    static ushort ExpectedRpmRaw(int i) => (ushort)((3000 + i * 10) / 0.25);
    static double ExpectedRpmPhysical(int i) => (3000 + i * 10);

    public CrossLanguageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"crosslang_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string TempFile(string name) => Path.Combine(_tempDir, name);

    // ── Writer: produces the standardized reference file ────────────────────

    private static void WriteReferenceFile(string path)
    {
        using var writer = MeasFile.CreateWriter(path);

        // File-level properties
        writer.Properties["TestSuite"] = "CrossLanguage";

        // Define ALL groups and channels before writing data
        // Group 1: Analog data
        var analog = writer.AddGroup("Analog");
        analog.Properties["SampleRate"] = 1000;

        var timestamps = analog.AddChannel<MeasTimestamp>("Time");
        var rpm = analog.AddChannel<float>("RPM");
        rpm.Properties["Unit"] = "1/min";
        var counter = analog.AddChannel<int>("Counter");

        // Group 2: CAN bus
        var can = writer.AddBusGroup("CAN_Test", new CanBusConfig { BaudRate = 500_000 });

        var engineFrame = can.DefineCanFrame("EngineData", frameId: EngineFrameId, payloadLength: 8);
        engineFrame.Signals.Add(new SignalDefinition
        {
            Name = "EngineRPM",
            StartBit = 0,
            BitLength = 16,
            Factor = 0.25,
            Unit = "rpm",
        });

        // Write analog data with incremental flush
        for (int flush = 0; flush < SampleCount / FlushInterval; flush++)
        {
            int start = flush * FlushInterval;
            for (int j = 0; j < FlushInterval; j++)
            {
                int i = start + j;
                timestamps.Write(new MeasTimestamp(ExpectedTimestampNs(i)));
                rpm.Write(ExpectedRpm(i));
                counter.Write(ExpectedCounter(i));
            }
            writer.Flush();
        }

        // Write CAN frames
        for (int i = 0; i < CanFrameCount; i++)
        {
            var ts = new MeasTimestamp(BaseTimestampNs + (long)i * 10_000_000); // 10ms intervals
            ushort rpmRaw = ExpectedRpmRaw(i);
            byte[] payload = [
                (byte)(rpmRaw & 0xFF), (byte)(rpmRaw >> 8),
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            ];
            can.WriteFrame(ts, EngineFrameId, payload);
        }
    }

    // ── Verifier: validates any reference file ──────────────────────────────

    private static void VerifyReferenceFile(string path, string writerLang)
    {
        using var reader = MeasFile.OpenRead(path);

        // File property (only present when the writer supports file-level properties)
        if (reader.Properties.ContainsKey("TestSuite"))
            Assert.Equal("CrossLanguage", reader.Properties["TestSuite"].ToString());

        // Group count
        Assert.True(reader.Groups.Count >= 2, $"[{writerLang}] Expected >=2 groups, got {reader.Groups.Count}");

        // ── Analog group ──
        var analog = reader["Analog"];
        Assert.NotNull(analog);
        Assert.Equal(3, analog.Channels.Count);

        // Channels
        var time = analog["Time"];
        var rpm = analog["RPM"];
        var counter = analog["Counter"];

        Assert.Equal(MeasDataType.Timestamp, time.DataType);
        Assert.Equal(MeasDataType.Float32, rpm.DataType);
        Assert.Equal(MeasDataType.Int32, counter.DataType);

        Assert.Equal(SampleCount, time.SampleCount);
        Assert.Equal(SampleCount, rpm.SampleCount);
        Assert.Equal(SampleCount, counter.SampleCount);

        // Spot-check values
        var rpmData = rpm.ReadAll<float>();
        var counterData = counter.ReadAll<int>();
        var timeData = time.ReadAll<MeasTimestamp>();

        // First, last, and middle samples
        Assert.Equal(ExpectedRpm(0), rpmData[0]);
        Assert.Equal(ExpectedRpm(9999), rpmData[9999]);
        Assert.Equal(ExpectedRpm(5000), rpmData[5000]);

        Assert.Equal(ExpectedCounter(0), counterData[0]);
        Assert.Equal(ExpectedCounter(9999), counterData[9999]);

        Assert.Equal(ExpectedTimestampNs(0), timeData[0].Nanoseconds);
        Assert.Equal(ExpectedTimestampNs(9999), timeData[9999].Nanoseconds);

        // Statistics
        var stats = rpm.Statistics;
        Assert.True(stats.HasValue, $"[{writerLang}] RPM statistics missing");
        var s = stats.Value;
        Assert.Equal(SampleCount, s.Count);
        Assert.Equal((double)ExpectedRpmFirst, s.Min, 1);
        Assert.Equal((double)ExpectedRpmLast, s.Max, 1);
        Assert.Equal((double)ExpectedRpmFirst, s.First, 1);
        Assert.Equal((double)ExpectedRpmLast, s.Last, 1);

        // Mean: arithmetic mean of 1000.0 + i*0.5 for i=0..9999
        //   = 1000.0 + 0.5 * (0+9999)/2 = 1000 + 2499.75 = 3499.75
        Assert.Equal(3499.75, s.Mean, 0.5);

        // Channel property
        Assert.Equal("1/min", rpm.Properties["Unit"].ToString());

        // ── CAN group ──
        var canGroup = reader["CAN_Test"];
        Assert.NotNull(canGroup);

        // Bus definition
        var busDef = canGroup.BusDefinition;
        Assert.NotNull(busDef);
        Assert.Equal(BusType.Can, busDef.BusConfig.BusType);

        // Frame definitions
        Assert.Single(busDef.Frames);
        var frame = busDef.Frames[0];
        Assert.Equal("EngineData", frame.Name);
        Assert.Equal(EngineFrameId, frame.FrameId);
        Assert.Single(frame.Signals);
        Assert.Equal("EngineRPM", frame.Signals[0].Name);
        Assert.Equal(0.25, frame.Signals[0].Factor);

        // Raw frames
        var rawFrames = canGroup["RawFrames"];
        Assert.NotNull(rawFrames);
        Assert.Equal(CanFrameCount, rawFrames.SampleCount);

        // Decode all EngineRPM signal values
        var decoded = canGroup.DecodeSignal("EngineRPM");
        Assert.Equal(CanFrameCount, decoded.Length);
        Assert.Equal(ExpectedRpmPhysical(0), decoded[0], 1);
        Assert.Equal(ExpectedRpmPhysical(99), decoded[99], 1);
    }

    // ── Test: C# writes and reads its own file ─────────────────────────────

    [Fact]
    public void CSharp_WriteAndRead_RoundTrip()
    {
        var path = TempFile("ref_csharp.meas");
        WriteReferenceFile(path);
        VerifyReferenceFile(path, "C#");
    }

    // ── Test: read file written by C ────────────────────────────────────────

    [Fact]
    public void Read_CWrittenFile()
    {
        var path = FindCrossLangFile("ref_c.meas");
        if (path == null)
        {
            // Skip gracefully in non-CI environments
            return;
        }
        VerifyReferenceFile(path, "C");
    }

    // ── Test: read file written by Python ───────────────────────────────────

    [Fact]
    public void Read_PythonWrittenFile()
    {
        var path = FindCrossLangFile("ref_python.meas");
        if (path == null)
        {
            return;
        }
        VerifyReferenceFile(path, "Python");
    }

    private static string? FindCrossLangFile(string filename)
    {
        string[] searchPaths =
        [
            filename,                                    // CWD = repo root (CI)
            Path.Combine("..", "..", filename),           // CWD = csharp/tests (local)
            Path.Combine("..", filename),                 // CWD = csharp/
        ];

        foreach (var p in searchPaths)
        {
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
