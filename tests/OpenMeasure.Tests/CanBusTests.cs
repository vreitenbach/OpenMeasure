using OpenMeasure.Bus;

namespace OpenMeasure.Tests;

/// <summary>
/// Tests for the new bus data model with structured frame/signal definitions.
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
    public void WriteAndRead_CanBusGroup_WithSignalDefinitions()
    {
        var path = TempFile();
        var start = new DateTimeOffset(2026, 3, 13, 10, 0, 0, TimeSpan.Zero);

        using (var writer = OmxFile.CreateWriter(path))
        {
            var can = writer.AddBusGroup("CAN_Engine", new CanBusConfig { BaudRate = 500_000 });

            // Define frame with signals — IDs belong to the FRAME, not the signal
            var engineFrame = can.DefineCanFrame("EngineData", frameId: 0x100, payloadLength: 8);
            engineFrame.Signals.Add(new SignalDefinition
            {
                Name = "EngineRPM",
                StartBit = 0,
                BitLength = 16,
                Factor = 0.25,
                Offset = 0,
                Unit = "rpm",
            });
            engineFrame.Signals.Add(new SignalDefinition
            {
                Name = "EngineTemp",
                StartBit = 16,
                BitLength = 8,
                Factor = 1.0,
                Offset = -40,
                Unit = "degC",
            });

            var brakeFrame = can.DefineCanFrame("BrakeData", frameId: 0x200, payloadLength: 8);
            brakeFrame.Signals.Add(new SignalDefinition
            {
                Name = "BrakePressure",
                StartBit = 0,
                BitLength = 16,
                Factor = 0.1,
                Offset = 0,
                Unit = "bar",
            });

            // Write frames with standardized wire format
            var ts = OmxTimestamp.FromDateTimeOffset(start);
            // Engine frame: RPM=3000 (raw=12000=0x2EE0), Temp=90°C (raw=130=0x82)
            can.WriteFrame(ts, 0x100, new byte[] { 0xE0, 0x2E, 0x82, 0x00, 0x00, 0x00, 0x00, 0x00 });

            ts = ts + TimeSpan.FromMilliseconds(1);
            can.WriteFrame(ts, 0x200, new byte[] { 0xC8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

            ts = ts + TimeSpan.FromMilliseconds(1);
            // Engine frame: RPM=4000 (raw=16000=0x3E80)
            can.WriteFrame(ts, 0x100, new byte[] { 0x80, 0x3E, 0x82, 0x00, 0x00, 0x00, 0x00, 0x00 });
        }

        // Read and verify
        using var reader = OmxFile.OpenRead(path);
        var g = reader["CAN_Engine"];

        // Bus definition is properly deserialized
        Assert.NotNull(g.BusDefinition);
        Assert.IsType<CanBusConfig>(g.BusDefinition.BusConfig);
        Assert.Equal(500_000, ((CanBusConfig)g.BusDefinition.BusConfig).BaudRate);

        // Frame definitions preserved
        Assert.Equal(2, g.BusDefinition.Frames.Count);
        var engine = g.BusDefinition.FindFrame(0x100);
        Assert.NotNull(engine);
        Assert.Equal("EngineData", engine.Name);
        Assert.IsType<CanFrameDefinition>(engine);
        Assert.Equal(2, engine.Signals.Count);

        var brake = g.BusDefinition.FindFrame(0x200);
        Assert.NotNull(brake);
        Assert.Equal("BrakeData", brake.Name);

        // Signal definitions preserved on frames, NOT on channels
        var rpmSig = engine.Signals.First(s => s.Name == "EngineRPM");
        Assert.Equal(0, rpmSig.StartBit);
        Assert.Equal(16, rpmSig.BitLength);
        Assert.Equal(0.25, rpmSig.Factor);
        Assert.Equal("rpm", rpmSig.Unit);

        // Decode signals from raw frames
        var rpmValues = g.DecodeSignal("EngineRPM");
        Assert.Equal(2, rpmValues.Length); // only 2 frames with ID 0x100
        Assert.Equal(3000.0, rpmValues[0], 1);
        Assert.Equal(4000.0, rpmValues[1], 1);

        var brakeValues = g.DecodeSignal("BrakePressure");
        Assert.Single(brakeValues);
        Assert.Equal(20.0, brakeValues[0], 1); // 0x00C8 = 200, * 0.1 = 20.0

        // Raw frames accessible
        var rawFrames = g["RawFrames"].ReadFrames();
        Assert.Equal(3, rawFrames.Count);
    }

    [Fact]
    public void WriteAndRead_MultipleBusTypes()
    {
        var path = TempFile("multi_bus.omx");
        var start = new DateTimeOffset(2026, 3, 13, 10, 0, 0, TimeSpan.Zero);
        var ts = OmxTimestamp.FromDateTimeOffset(start);

        using (var writer = OmxFile.CreateWriter(path))
        {
            // CAN bus
            var can = writer.AddBusGroup("CAN1", new CanBusConfig { BaudRate = 500_000 });
            can.DefineCanFrame("Msg1", 0x100, 8);
            can.WriteFrame(ts, 0x100, new byte[8]);

            // CAN-FD bus
            var canFd = writer.AddBusGroup("CAN_FD1", new CanFdBusConfig
            {
                ArbitrationBaudRate = 500_000,
                DataBaudRate = 2_000_000,
            });
            canFd.DefineCanFdFrame("DiagResp", 0x7E8, 64, bitRateSwitch: true);
            canFd.WriteFrame(ts, 0x7E8, new byte[64], FrameFlags.FdFrame | FrameFlags.BrsFlag);

            // LIN bus
            var lin = writer.AddBusGroup("LIN1", new LinBusConfig { BaudRate = 19_200 });
            lin.DefineLinFrame("WindowStatus", 0x3C, 8);
            lin.WriteFrame(ts, 0x3C, new byte[8]);
        }

        using var reader = OmxFile.OpenRead(path);
        Assert.Equal(3, reader.Groups.Count);

        Assert.IsType<CanBusConfig>(reader["CAN1"].BusDefinition!.BusConfig);
        Assert.IsType<CanFdBusConfig>(reader["CAN_FD1"].BusDefinition!.BusConfig);
        Assert.IsType<LinBusConfig>(reader["LIN1"].BusDefinition!.BusConfig);

        Assert.Equal(2_000_000, ((CanFdBusConfig)reader["CAN_FD1"].BusDefinition!.BusConfig).DataBaudRate);
        Assert.IsType<CanFdFrameDefinition>(reader["CAN_FD1"].BusDefinition!.Frames[0]);
        Assert.IsType<LinFrameDefinition>(reader["LIN1"].BusDefinition!.Frames[0]);
    }

    [Fact]
    public void WriteAndRead_FrameWithPduAndSignals()
    {
        var path = TempFile("pdu.omx");
        var ts = OmxTimestamp.Now;

        using (var writer = OmxFile.CreateWriter(path))
        {
            var can = writer.AddBusGroup("CAN1", new CanBusConfig());

            var frame = can.DefineCanFrame("DiagReq", 0x700, 8);
            frame.Pdus.Add(new PduDefinition
            {
                Name = "DiagPdu",
                PduId = 0x01,
                ByteOffset = 0,
                Length = 8,
                Signals =
                [
                    new() { Name = "ServiceId", StartBit = 0, BitLength = 8 },
                    new() { Name = "SubFunction", StartBit = 8, BitLength = 8 },
                ],
            });

            can.WriteFrame(ts, 0x700, new byte[] { 0x22, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
        }

        using var reader = OmxFile.OpenRead(path);
        var g = reader["CAN1"];
        Assert.NotNull(g.BusDefinition);

        var frameDef = g.BusDefinition.FindFrame(0x700)!;
        Assert.Single(frameDef.Pdus);
        Assert.Equal("DiagPdu", frameDef.Pdus[0].Name);
        Assert.Equal(2, frameDef.Pdus[0].Signals.Count);
    }

    [Fact]
    public void WriteAndRead_LargeCanBusWithDecoding()
    {
        var path = TempFile("can_large.omx");
        var start = new DateTimeOffset(2026, 3, 13, 10, 0, 0, TimeSpan.Zero);
        int frameCount = 10_000;

        using (var writer = OmxFile.CreateWriter(path))
        {
            var can = writer.AddBusGroup("CAN1", new CanBusConfig { BaudRate = 500_000 });

            var engineFrame = can.DefineCanFrame("EngineData", 0x100, 8);
            engineFrame.Signals.Add(new SignalDefinition
            {
                Name = "RPM",
                StartBit = 0,
                BitLength = 16,
                Factor = 0.25,
                Unit = "rpm",
            });

            can.DefineCanFrame("BrakeData", 0x200, 8);
            can.DefineCanFrame("SteeringData", 0x300, 8);

            var ts = OmxTimestamp.FromDateTimeOffset(start);
            uint[] ids = [0x100, 0x200, 0x300];

            for (int i = 0; i < frameCount; i++)
            {
                uint id = ids[i % 3];
                var payload = new byte[8];
                if (id == 0x100)
                {
                    // RPM: ramp from 800 to 6500
                    float rpm = 800 + (float)i / frameCount * 5700;
                    ushort rpmRaw = (ushort)(rpm / 0.25);
                    payload[0] = (byte)(rpmRaw & 0xFF);
                    payload[1] = (byte)(rpmRaw >> 8);
                }
                can.WriteFrame(ts, id, payload);
                ts = ts + TimeSpan.FromMilliseconds(1);
            }
        }

        using var reader = OmxFile.OpenRead(path);
        var g = reader["CAN1"];

        var rawFrames = g["RawFrames"].ReadFrames();
        Assert.Equal(frameCount, rawFrames.Count);

        var rpmValues = g.DecodeSignal("RPM");
        // Only 1/3 of frames are ID 0x100
        Assert.True(rpmValues.Length > frameCount / 4);
        Assert.True(rpmValues.Length < frameCount / 2);

        // First and last RPM values should be in expected range
        Assert.InRange(rpmValues[0], 700, 1000);
        Assert.InRange(rpmValues[^1], 5500, 7000);
    }

    [Fact]
    public void BackwardCompatibility_RawChannelWithoutBusDef()
    {
        // Old-style raw channels still work without bus definition
        var path = TempFile("compat.omx");
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
        var g = reader["CAN_Bus1"];
        Assert.Null(g.BusDefinition); // No bus definition
        Assert.Equal(2, g["RawFrames"].SampleCount);
        Assert.Equal(frames[0], g["RawFrames"].ReadFrames()[0]);
    }
}
