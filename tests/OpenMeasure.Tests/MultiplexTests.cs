using OpenMeasure.Bus;
using OpenMeasure.Format;

namespace OpenMeasure.Tests;

/// <summary>
/// Tests for multiplexed signals and container PDUs.
/// </summary>
public class MultiplexTests : IDisposable
{
    private readonly string _tempDir;

    public MultiplexTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"omx_mux_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string TempFile(string name = "mux.omx") => Path.Combine(_tempDir, name);

    [Fact]
    public void WriteAndRead_MultiplexedSignals()
    {
        var path = TempFile();
        var ts = OmxTimestamp.Now;

        using (var writer = OmxFile.CreateWriter(path))
        {
            var can = writer.AddBusGroup("CAN1", new CanBusConfig());

            var frame = can.DefineCanFrame("MuxMsg", 0x300, 8);

            // Multiplexer signal: byte 0
            frame.Signals.Add(new SignalDefinition
            {
                Name = "MuxSelector",
                StartBit = 0,
                BitLength = 8,
                IsMultiplexer = true,
            });

            // MUX=0: Temperature at bytes 1-2
            frame.Signals.Add(new SignalDefinition
            {
                Name = "Temperature",
                StartBit = 8,
                BitLength = 16,
                Factor = 0.1,
                Offset = -40,
                Unit = "degC",
                MultiplexCondition = new MultiplexCondition
                {
                    MultiplexerSignalName = "MuxSelector",
                    LowValue = 0,
                    HighValue = 0,
                },
            });

            // MUX=1: Pressure at bytes 1-2
            frame.Signals.Add(new SignalDefinition
            {
                Name = "Pressure",
                StartBit = 8,
                BitLength = 16,
                Factor = 0.01,
                Offset = 0,
                Unit = "bar",
                MultiplexCondition = new MultiplexCondition
                {
                    MultiplexerSignalName = "MuxSelector",
                    LowValue = 1,
                    HighValue = 1,
                },
            });

            // MUX=0: Temperature = raw 900, physical = 900*0.1-40 = 50°C
            can.WriteFrame(ts, 0x300, new byte[] { 0x00, 0x84, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00 });

            ts = ts + TimeSpan.FromMilliseconds(1);
            // MUX=1: Pressure = raw 1500, physical = 1500*0.01 = 15.0 bar
            can.WriteFrame(ts, 0x300, new byte[] { 0x01, 0xDC, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00 });

            ts = ts + TimeSpan.FromMilliseconds(1);
            // MUX=0: Temperature = raw 1000, physical = 1000*0.1-40 = 60°C
            can.WriteFrame(ts, 0x300, new byte[] { 0x00, 0xE8, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00 });
        }

        using var reader = OmxFile.OpenRead(path);
        var g = reader["CAN1"];

        // Temperature should only decode from MUX=0 frames
        var temps = g.DecodeSignal("Temperature");
        Assert.Equal(2, temps.Length);
        Assert.Equal(50.0, temps[0], 1);
        Assert.Equal(60.0, temps[1], 1);

        // Pressure should only decode from MUX=1 frames
        var pressures = g.DecodeSignal("Pressure");
        Assert.Single(pressures);
        Assert.Equal(15.0, pressures[0], 1);

        // MuxSelector is always present (non-multiplexed)
        var muxVals = g.DecodeSignal("MuxSelector");
        Assert.Equal(3, muxVals.Length);
    }

    [Fact]
    public void WriteAndRead_ValueDescriptions()
    {
        var path = TempFile("val_desc.omx");
        var ts = OmxTimestamp.Now;

        using (var writer = OmxFile.CreateWriter(path))
        {
            var can = writer.AddBusGroup("CAN1", new CanBusConfig());

            var frame = can.DefineCanFrame("GearBox", 0x150, 8);
            frame.Signals.Add(new SignalDefinition
            {
                Name = "GearPosition",
                StartBit = 0,
                BitLength = 4,
                ValueDescriptions = new()
                {
                    [0] = "Park",
                    [1] = "Reverse",
                    [2] = "Neutral",
                    [3] = "Drive",
                    [4] = "Sport",
                },
            });

            can.WriteFrame(ts, 0x150, new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
        }

        using var reader = OmxFile.OpenRead(path);
        var g = reader["CAN1"];
        var sig = g.BusDefinition!.FindSignal("GearPosition");
        Assert.NotNull(sig);
        Assert.Equal("Drive", sig.Value.Signal.ValueDescriptions![3]);

        var values = g.DecodeSignal("GearPosition");
        Assert.Single(values);
        Assert.Equal(3.0, values[0], 1);
    }

    [Fact]
    public void MultiplexConfig_Serialization_Roundtrip()
    {
        var def = new BusChannelDefinition
        {
            BusConfig = new CanBusConfig(),
            RawFrameChannelName = "Raw",
            TimestampChannelName = "TS",
        };

        def.Frames.Add(new CanFrameDefinition
        {
            Name = "MuxFrame",
            FrameId = 0x300,
            PayloadLength = 8,
            Pdus =
            [
                new()
                {
                    Name = "MuxPdu",
                    PduId = 1,
                    ByteOffset = 0,
                    Length = 8,
                    Multiplexing = new MultiplexConfig
                    {
                        MultiplexerSignalName = "Selector",
                        MuxGroups = new()
                        {
                            [0] = ["TempA", "TempB"],
                            [1] = ["PressA", "PressB"],
                            [2] = ["VoltA"],
                        },
                    },
                    Signals =
                    [
                        new() { Name = "Selector", StartBit = 0, BitLength = 8, IsMultiplexer = true },
                        new()
                        {
                            Name = "TempA", StartBit = 8, BitLength = 16,
                            MultiplexCondition = new() { MultiplexerSignalName = "Selector", LowValue = 0, HighValue = 0 },
                        },
                    ],
                },
            ],
        });

        var encoded = BusMetadataEncoder.Encode(def);
        var decoded = BusMetadataEncoder.Decode(encoded);

        var pdu = decoded.Frames[0].Pdus[0];
        Assert.NotNull(pdu.Multiplexing);
        Assert.Equal("Selector", pdu.Multiplexing.MultiplexerSignalName);
        Assert.Equal(3, pdu.Multiplexing.MuxGroups.Count);
        Assert.Equal(["TempA", "TempB"], pdu.Multiplexing.MuxGroups[0]);
        Assert.Equal(["VoltA"], pdu.Multiplexing.MuxGroups[2]);

        // Nested mux condition preserved
        var tempA = pdu.Signals.First(s => s.Name == "TempA");
        Assert.NotNull(tempA.MultiplexCondition);
        Assert.Equal(0, tempA.MultiplexCondition.LowValue);
    }

    [Fact]
    public void SecOcConfig_Serialization_Roundtrip()
    {
        var def = new BusChannelDefinition
        {
            BusConfig = new CanFdBusConfig(),
            RawFrameChannelName = "Raw",
            TimestampChannelName = "TS",
        };

        def.Frames.Add(new CanFdFrameDefinition
        {
            Name = "SecuredMsg",
            FrameId = 0x500,
            PayloadLength = 64,
            Pdus =
            [
                new()
                {
                    Name = "SecuredPdu",
                    PduId = 1,
                    ByteOffset = 0,
                    Length = 64,
                    SecOc = new SecOcConfig
                    {
                        Algorithm = SecOcAlgorithm.HmacSha256,
                        FreshnessValueStartBit = 256,
                        FreshnessValueTruncatedLength = 24,
                        FreshnessValueFullLength = 64,
                        FreshnessType = FreshnessValueType.CounterAndTimestamp,
                        MacStartBit = 280,
                        MacTruncatedLength = 48,
                        MacFullLength = 256,
                        AuthenticPayloadLength = 256,
                        DataId = 0x1234,
                        AuthenticationBuildAttempts = 3,
                        UseFreshnessValueManager = true,
                        KeyId = 42,
                    },
                },
            ],
        });

        var encoded = BusMetadataEncoder.Encode(def);
        var decoded = BusMetadataEncoder.Decode(encoded);

        var secOc = decoded.Frames[0].Pdus[0].SecOc!;
        Assert.Equal(SecOcAlgorithm.HmacSha256, secOc.Algorithm);
        Assert.Equal(24, secOc.FreshnessValueTruncatedLength);
        Assert.Equal(FreshnessValueType.CounterAndTimestamp, secOc.FreshnessType);
        Assert.Equal(48, secOc.MacTruncatedLength);
        Assert.Equal(256, secOc.MacFullLength);
        Assert.Equal(0x1234u, secOc.DataId);
        Assert.Equal(3, secOc.AuthenticationBuildAttempts);
        Assert.True(secOc.UseFreshnessValueManager);
        Assert.Equal(42u, secOc.KeyId);
    }
}
