using MeasFlow.Bus;
using MeasFlow.Format;

namespace MeasFlow.Tests;

/// <summary>
/// Tests for bus model serialization roundtrip and signal decoding.
/// </summary>
public class BusModelTests
{
    [Fact]
    public void BusMetadataEncoder_Roundtrip_CanBus()
    {
        var def = new BusChannelDefinition
        {
            BusConfig = new CanBusConfig { BaudRate = 500_000, IsExtendedId = false },
            RawFrameChannelName = "RawFrames",
            TimestampChannelName = "Timestamp",
        };

        def.Frames.Add(new CanFrameDefinition
        {
            Name = "EngineData",
            FrameId = 0x100,
            PayloadLength = 8,
            Direction = FrameDirection.Rx,
            Signals =
            [
                new() { Name = "RPM", StartBit = 0, BitLength = 16, Factor = 0.25, Unit = "rpm" },
                new() { Name = "Temp", StartBit = 16, BitLength = 8, Factor = 1.0, Offset = -40, Unit = "degC" },
            ],
        });

        def.ValueTables.Add(new ValueTable
        {
            Name = "GearStates",
            Entries = new() { [0] = "Park", [1] = "Reverse", [2] = "Neutral", [3] = "Drive" },
        });

        var encoded = BusMetadataEncoder.Encode(def);
        var decoded = BusMetadataEncoder.Decode(encoded);

        Assert.IsType<CanBusConfig>(decoded.BusConfig);
        Assert.Equal(500_000, ((CanBusConfig)decoded.BusConfig).BaudRate);
        Assert.Equal("RawFrames", decoded.RawFrameChannelName);
        Assert.Single(decoded.Frames);
        Assert.IsType<CanFrameDefinition>(decoded.Frames[0]);

        var frame = decoded.Frames[0];
        Assert.Equal("EngineData", frame.Name);
        Assert.Equal(0x100u, frame.FrameId);
        Assert.Equal(2, frame.Signals.Count);

        var rpm = frame.Signals[0];
        Assert.Equal("RPM", rpm.Name);
        Assert.Equal(0.25, rpm.Factor);
        Assert.Equal("rpm", rpm.Unit);

        Assert.Single(decoded.ValueTables);
        Assert.Equal("Drive", decoded.ValueTables[0].GetDescription(3));
    }

    [Fact]
    public void BusMetadataEncoder_Roundtrip_CanFdWithPduAndE2E()
    {
        var def = new BusChannelDefinition
        {
            BusConfig = new CanFdBusConfig
            {
                ArbitrationBaudRate = 500_000,
                DataBaudRate = 2_000_000,
                IsExtendedId = true,
            },
            RawFrameChannelName = "Raw",
            TimestampChannelName = "TS",
        };

        def.Frames.Add(new CanFdFrameDefinition
        {
            Name = "SecureMsg",
            FrameId = 0x7E8,
            PayloadLength = 64,
            BitRateSwitch = true,
            Pdus =
            [
                new()
                {
                    Name = "SecurePdu",
                    PduId = 1,
                    ByteOffset = 0,
                    Length = 32,
                    E2EProtection = new E2EProtection
                    {
                        Profile = E2EProfile.Profile01,
                        CrcStartBit = 0,
                        CrcBitLength = 8,
                        CounterStartBit = 8,
                        CounterBitLength = 4,
                        DataId = 0x0100,
                        CrcPolynomial = 0x1D,
                    },
                    SecOc = new SecOcConfig
                    {
                        Algorithm = SecOcAlgorithm.CmacAes128,
                        FreshnessValueStartBit = 192,
                        FreshnessValueTruncatedLength = 16,
                        MacStartBit = 208,
                        MacTruncatedLength = 28,
                        AuthenticPayloadLength = 192,
                        DataId = 42,
                        KeyId = 7,
                    },
                    Signals = [new() { Name = "DiagData", StartBit = 16, BitLength = 48 }],
                },
            ],
        });

        var encoded = BusMetadataEncoder.Encode(def);
        var decoded = BusMetadataEncoder.Decode(encoded);

        Assert.IsType<CanFdBusConfig>(decoded.BusConfig);
        var cfg = (CanFdBusConfig)decoded.BusConfig;
        Assert.Equal(2_000_000, cfg.DataBaudRate);
        Assert.True(cfg.IsExtendedId);

        var frame = decoded.Frames[0];
        Assert.IsType<CanFdFrameDefinition>(frame);
        Assert.True(((CanFdFrameDefinition)frame).BitRateSwitch);

        var pdu = frame.Pdus[0];
        Assert.Equal("SecurePdu", pdu.Name);
        Assert.NotNull(pdu.E2EProtection);
        Assert.Equal(E2EProfile.Profile01, pdu.E2EProtection.Profile);
        Assert.Equal(0x1Du, pdu.E2EProtection.CrcPolynomial);

        Assert.NotNull(pdu.SecOc);
        Assert.Equal(SecOcAlgorithm.CmacAes128, pdu.SecOc.Algorithm);
        Assert.Equal(28, pdu.SecOc.MacTruncatedLength);
        Assert.Equal(42u, pdu.SecOc.DataId);
        Assert.Equal(7u, pdu.SecOc.KeyId);
    }

    [Fact]
    public void BusMetadataEncoder_Roundtrip_ContainerPdu()
    {
        var def = new BusChannelDefinition
        {
            BusConfig = new CanFdBusConfig(),
            RawFrameChannelName = "Raw",
            TimestampChannelName = "TS",
        };

        def.Frames.Add(new CanFdFrameDefinition
        {
            Name = "ContainerFrame",
            FrameId = 0x400,
            PayloadLength = 64,
            Pdus =
            [
                new()
                {
                    Name = "Container",
                    PduId = 1,
                    ByteOffset = 0,
                    Length = 64,
                    IsContainerPdu = true,
                    ContainedPdus =
                    [
                        new()
                        {
                            Name = "StatusPdu",
                            HeaderId = 0x10,
                            Length = 4,
                            Signals = [new() { Name = "Status", StartBit = 0, BitLength = 8 }],
                        },
                        new()
                        {
                            Name = "TempPdu",
                            HeaderId = 0x20,
                            Length = 4,
                            Signals = [new() { Name = "Temp", StartBit = 0, BitLength = 16, Factor = 0.1 }],
                        },
                    ],
                },
            ],
        });

        var encoded = BusMetadataEncoder.Encode(def);
        var decoded = BusMetadataEncoder.Decode(encoded);

        var pdu = decoded.Frames[0].Pdus[0];
        Assert.True(pdu.IsContainerPdu);
        Assert.Equal(2, pdu.ContainedPdus.Count);
        Assert.Equal("StatusPdu", pdu.ContainedPdus[0].Name);
        Assert.Equal(0x10u, pdu.ContainedPdus[0].HeaderId);
        Assert.Single(pdu.ContainedPdus[0].Signals);
    }

    [Fact]
    public void BusMetadataEncoder_Roundtrip_FlexRay()
    {
        var def = new BusChannelDefinition
        {
            BusConfig = new FlexRayBusConfig { CycleTimeUs = 5000, MacroticksPerCycle = 3636 },
            RawFrameChannelName = "Raw",
            TimestampChannelName = "TS",
        };

        def.Frames.Add(new FlexRayFrameDefinition
        {
            Name = "SyncMsg",
            FrameId = 1,   // Slot 1
            PayloadLength = 32,
            CycleCount = 0,
            Channel = FlexRayChannel.AB,
        });

        var encoded = BusMetadataEncoder.Encode(def);
        var decoded = BusMetadataEncoder.Decode(encoded);

        Assert.IsType<FlexRayBusConfig>(decoded.BusConfig);
        Assert.Equal(5000, ((FlexRayBusConfig)decoded.BusConfig).CycleTimeUs);
        Assert.IsType<FlexRayFrameDefinition>(decoded.Frames[0]);
        Assert.Equal(FlexRayChannel.AB, ((FlexRayFrameDefinition)decoded.Frames[0]).Channel);
    }

    [Fact]
    public void SignalDecoder_Intel_ByteOrder()
    {
        // 16-bit signal at bit 0, Intel byte order
        var signal = new SignalDefinition
        {
            Name = "Test",
            StartBit = 0,
            BitLength = 16,
            Factor = 0.25,
        };

        byte[] payload = [0xE0, 0x2E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        // 0x2EE0 = 12000, * 0.25 = 3000
        double value = SignalDecoder.DecodeSignal(payload, signal);
        Assert.Equal(3000.0, value, 1);
    }

    [Fact]
    public void SignalDecoder_SignedValue()
    {
        var signal = new SignalDefinition
        {
            Name = "Temp",
            StartBit = 0,
            BitLength = 8,
            DataType = SignalDataType.Signed,
            Factor = 1.0,
            Offset = 0,
        };

        byte[] payload = [0xF0, 0x00]; // -16 as signed byte
        double value = SignalDecoder.DecodeSignal(payload, signal);
        Assert.Equal(-16.0, value, 1);
    }

    [Fact]
    public void SignalDecoder_WithFactorAndOffset()
    {
        var signal = new SignalDefinition
        {
            Name = "Temp",
            StartBit = 0,
            BitLength = 8,
            Factor = 1.0,
            Offset = -40,
        };

        byte[] payload = [130, 0x00]; // raw=130, physical = 130 * 1.0 - 40 = 90
        double value = SignalDecoder.DecodeSignal(payload, signal);
        Assert.Equal(90.0, value, 1);
    }

    [Fact]
    public void SignalDecoder_MiddleBits()
    {
        // Signal at bit 8, length 4 (second nibble of second byte)
        var signal = new SignalDefinition
        {
            Name = "Nibble",
            StartBit = 8,
            BitLength = 4,
        };

        byte[] payload = [0x00, 0x0A]; // bits 8-11 = 0xA = 10
        double value = SignalDecoder.DecodeSignal(payload, signal);
        Assert.Equal(10.0, value, 1);
    }

    [Fact]
    public void BusChannelDefinition_FindSignal()
    {
        var def = new BusChannelDefinition
        {
            BusConfig = new CanBusConfig(),
            RawFrameChannelName = "Raw",
            TimestampChannelName = "TS",
        };

        def.Frames.Add(new CanFrameDefinition
        {
            Name = "Frame1",
            FrameId = 0x100,
            PayloadLength = 8,
            Signals = [new() { Name = "Sig1", StartBit = 0, BitLength = 8 }],
        });

        def.Frames.Add(new CanFrameDefinition
        {
            Name = "Frame2",
            FrameId = 0x200,
            PayloadLength = 8,
            Pdus =
            [
                new()
                {
                    Name = "Pdu1",
                    PduId = 1,
                    ByteOffset = 0,
                    Length = 8,
                    Signals = [new() { Name = "PduSig1", StartBit = 0, BitLength = 16 }],
                },
            ],
        });

        var sig1 = def.FindSignal("Sig1");
        Assert.NotNull(sig1);
        Assert.Equal(0x100u, sig1.Value.Frame.FrameId);
        Assert.Null(sig1.Value.Pdu);

        var pduSig = def.FindSignal("PduSig1");
        Assert.NotNull(pduSig);
        Assert.Equal(0x200u, pduSig.Value.Frame.FrameId);
        Assert.NotNull(pduSig.Value.Pdu);
        Assert.Equal("Pdu1", pduSig.Value.Pdu.Name);

        Assert.Null(def.FindSignal("NonExistent"));
    }

    [Fact]
    public void MeasValue_BinaryType_RoundTrip()
    {
        byte[] data = [1, 2, 3, 4, 5];
        MeasValue value = data;
        Assert.Equal(MeasDataType.Binary, value.Type);
        Assert.Equal(data, value.AsBinary());
        Assert.Equal("byte[5]", value.ToString());
    }
}
