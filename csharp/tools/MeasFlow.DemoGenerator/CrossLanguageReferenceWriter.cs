using MeasFlow;
using MeasFlow.Bus;

/// <summary>
/// Writes the standardized cross-language reference file.
/// The same data must be produced by C, C#, and Python writers.
/// </summary>
public static class CrossLanguageReferenceWriter
{
    public const int SampleCount = 10_000;
    public const int FlushInterval = 1_000;
    public const long BaseTimestampNs = 1_700_000_000_000_000_000L;
    public const uint EngineFrameId = 0x100;
    public const int CanFrameCount = 100;

    public static void Write(string path)
    {
        using var writer = MeasFile.CreateWriter(path);

        // File-level properties
        writer.Properties["TestSuite"] = "CrossLanguage";

        // Group 1: Analog data
        var analog = writer.AddGroup("Analog");
        analog.Properties["SampleRate"] = 1000;

        var timestamps = analog.AddChannel<MeasTimestamp>("Time");
        var rpm = analog.AddChannel<float>("RPM");
        rpm.Properties["Unit"] = "1/min";
        var counter = analog.AddChannel<int>("Counter");

        // Group 2: CAN bus — define before first flush
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
                timestamps.Write(new MeasTimestamp(BaseTimestampNs + (long)i * 1_000));
                rpm.Write(1000.0f + i * 0.5f);
                counter.Write(i * 3 - 5000);
            }
            writer.Flush();
        }

        // Write CAN frames
        for (int i = 0; i < CanFrameCount; i++)
        {
            var ts = new MeasTimestamp(BaseTimestampNs + (long)i * 10_000_000);
            ushort rpmRaw = (ushort)((3000 + i * 10) / 0.25);
            byte[] payload =
            [
                (byte)(rpmRaw & 0xFF), (byte)(rpmRaw >> 8),
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            ];
            can.WriteFrame(ts, EngineFrameId, payload);
        }
    }
}
