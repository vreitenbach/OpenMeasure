using OpenMeasure.Tests.TestData;

namespace OpenMeasure.Tests;

/// <summary>
/// Tests for raw bus data (CAN/LIN frames) with linked decoded signals,
/// similar to MDF4 signal groups.
/// </summary>
public class CanBusTests : IDisposable
{
    private readonly string _tempDir;

    public CanBusTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"omx_can_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string TempFile(string name = "can.omx") => Path.Combine(_tempDir, name);

    [Fact]
    public void WriteAndRead_RawCanFrames()
    {
        var path = TempFile();
        byte[][] frames =
        [
            [0x00, 0x01, 8, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80],
            [0x00, 0x02, 8, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22],
        ];

        using (var writer = OmxFile.CreateWriter(path))
        {
            var group = writer.AddGroup("CAN_Bus1");
            var raw = group.AddRawChannel("RawFrames");
            foreach (var frame in frames)
                raw.WriteFrame(frame);
        }

        using var reader = OmxFile.OpenRead(path);
        var channel = reader["CAN_Bus1"]["RawFrames"];
        Assert.Equal(OmxDataType.Binary, channel.DataType);
        Assert.Equal(2, channel.SampleCount);

        var readFrames = channel.ReadFrames();
        Assert.Equal(2, readFrames.Count);
        Assert.Equal(frames[0], readFrames[0]);
        Assert.Equal(frames[1], readFrames[1]);
    }

    [Fact]
    public void WriteAndRead_RawFramesWithTimestamps()
    {
        var path = TempFile("can_ts.omx");
        var start = new DateTimeOffset(2026, 3, 13, 10, 0, 0, TimeSpan.Zero);
        var (frames, timestamps) = MeasurementDataGenerator.GenerateCanFrames(start, 100, frameRateHz: 1000);

        using (var writer = OmxFile.CreateWriter(path))
        {
            var group = writer.AddGroup("CAN");
            group.AddChannel<OmxTimestamp>("Timestamp").Write(timestamps.AsSpan());
            var raw = group.AddRawChannel("Frames");
            foreach (var frame in frames)
                raw.WriteFrame(frame);
        }

        using var reader = OmxFile.OpenRead(path);
        var readTs = reader["CAN"]["Timestamp"].ReadAll<OmxTimestamp>();
        var readFrames = reader["CAN"]["Frames"].ReadFrames();

        Assert.Equal(100, readTs.Length);
        Assert.Equal(100, readFrames.Count);
        Assert.Equal(timestamps[0], readTs[0]);
        Assert.Equal(timestamps[^1], readTs[^1]);
        Assert.Equal(frames[0], readFrames[0]);
    }

    [Fact]
    public void WriteAndRead_RawWithDecodedSignals()
    {
        // MDF4-style: raw CAN frames + decoded signals in the same group
        var path = TempFile("can_signals.omx");
        var start = new DateTimeOffset(2026, 3, 13, 10, 0, 0, TimeSpan.Zero);
        var (frames, timestamps) = MeasurementDataGenerator.GenerateCanFrames(start, 500, 1000);
        var decodedRpm = MeasurementDataGenerator.DecodeRpmFromCanFrames(frames);

        // Filter timestamps for RPM frames only (every 5th frame = CAN ID 0x100)
        var rpmTimestamps = new List<OmxTimestamp>();
        for (int i = 0; i < frames.Length; i++)
        {
            ushort canId = (ushort)(frames[i][0] | (frames[i][1] << 8));
            if (canId == 0x100)
                rpmTimestamps.Add(timestamps[i]);
        }

        using (var writer = OmxFile.CreateWriter(path))
        {
            var group = writer.AddGroup("CAN_Engine");
            group.Properties["BusType"] = "CAN 2.0B";
            group.Properties["Baudrate"] = 500000;

            // Raw frames with their timestamps
            group.AddChannel<OmxTimestamp>("FrameTimestamp").Write(timestamps.AsSpan());
            var rawChannel = group.AddRawChannel("RawFrames");
            foreach (var frame in frames)
                rawChannel.WriteFrame(frame);

            // Decoded signal linked to raw channel
            var rpmCh = group.AddSignalChannel<float>(
                name: "EngineRPM",
                sourceChannelName: "RawFrames",
                startBit: 24,    // byte offset 3, bit 0
                bitLength: 16,
                factor: 0.25,
                offset: 0.0);
            rpmCh.Properties["Unit"] = "1/min";
            rpmCh.Properties["CanId"] = 0x100;
            rpmCh.Write(decodedRpm.AsSpan());

            // Signal timestamps (only for frames with CAN ID 0x100)
            group.AddChannel<OmxTimestamp>("RPM_Timestamp").Write(rpmTimestamps.ToArray().AsSpan());
        }

        // Read and verify
        using var reader = OmxFile.OpenRead(path);
        var g = reader["CAN_Engine"];

        // Verify group properties
        Assert.Equal("CAN 2.0B", g.Properties["BusType"].AsString());
        Assert.Equal(500000, g.Properties["Baudrate"].AsInt32());

        // Verify raw frames
        var readFrames = g["RawFrames"].ReadFrames();
        Assert.Equal(500, readFrames.Count);
        Assert.Equal(frames[0], readFrames[0]);

        // Verify decoded signal
        var rpmChannel = g["EngineRPM"];
        Assert.Equal("RawFrames", rpmChannel.SourceChannelName);
        Assert.Equal("1/min", rpmChannel.Properties["Unit"].AsString());
        Assert.Equal(0x100, rpmChannel.Properties["CanId"].AsInt32());

        var readRpm = rpmChannel.ReadAll<float>();
        Assert.Equal(decodedRpm.Length, readRpm.Length);
        Assert.Equal(decodedRpm[0], readRpm[0], precision: 2);
        Assert.Equal(decodedRpm[^1], readRpm[^1], precision: 2);

        // Verify signal decoding properties
        Assert.Equal(24, rpmChannel.Properties["omx.start_bit"].AsInt32());
        Assert.Equal(16, rpmChannel.Properties["omx.bit_length"].AsInt32());
        Assert.Equal(0.25, rpmChannel.Properties["omx.factor"].AsFloat64());
    }

    [Fact]
    public void WriteAndRead_MultipleCanBuses()
    {
        var path = TempFile("multi_bus.omx");
        var start = new DateTimeOffset(2026, 3, 13, 10, 0, 0, TimeSpan.Zero);

        using (var writer = OmxFile.CreateWriter(path))
        {
            // CAN Bus 1 - Engine
            var canEngine = writer.AddGroup("CAN1_Engine");
            canEngine.Properties["BusType"] = "CAN";
            var (frames1, ts1) = MeasurementDataGenerator.GenerateCanFrames(start, 50);
            canEngine.AddChannel<OmxTimestamp>("Timestamp").Write(ts1.AsSpan());
            var raw1 = canEngine.AddRawChannel("Frames");
            foreach (var f in frames1) raw1.WriteFrame(f);

            // CAN Bus 2 - Chassis
            var canChassis = writer.AddGroup("CAN2_Chassis");
            canChassis.Properties["BusType"] = "CAN";
            var (frames2, ts2) = MeasurementDataGenerator.GenerateCanFrames(start, 30, 500);
            canChassis.AddChannel<OmxTimestamp>("Timestamp").Write(ts2.AsSpan());
            var raw2 = canChassis.AddRawChannel("Frames");
            foreach (var f in frames2) raw2.WriteFrame(f);

            // LIN Bus
            var linGroup = writer.AddGroup("LIN_Body");
            linGroup.Properties["BusType"] = "LIN";
            linGroup.Properties["Baudrate"] = 19200;
            var linTs = MeasurementDataGenerator.GenerateTimestamps(start, 20, 100);
            linGroup.AddChannel<OmxTimestamp>("Timestamp").Write(linTs.AsSpan());
            var linRaw = linGroup.AddRawChannel("Frames");
            // LIN frames are shorter (typically 2-8 bytes)
            for (int i = 0; i < 20; i++)
                linRaw.WriteFrame(new byte[] { (byte)i, 0x01, (byte)(i * 2) });
        }

        using var reader = OmxFile.OpenRead(path);
        Assert.Equal(3, reader.Groups.Count);
        Assert.Equal("CAN", reader["CAN1_Engine"].Properties["BusType"].AsString());
        Assert.Equal("LIN", reader["LIN_Body"].Properties["BusType"].AsString());
        Assert.Equal(50, reader["CAN1_Engine"]["Frames"].SampleCount);
        Assert.Equal(30, reader["CAN2_Chassis"]["Frames"].SampleCount);
        Assert.Equal(20, reader["LIN_Body"]["Frames"].SampleCount);
    }
}
