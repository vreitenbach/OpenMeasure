using System.Buffers.Binary;

namespace MeasFlow.Bus;

/// <summary>
/// Writer for a bus data group. Manages raw frame storage with standardized
/// wire format and structured frame/signal definitions.
/// </summary>
public sealed class BusGroupWriter
{
    private readonly GroupWriter _group;
    private readonly ChannelWriter<MeasTimestamp> _timestampChannel;
    private readonly RawChannelWriter _rawChannel;

    public BusChannelDefinition BusDefinition { get; }
    public GroupWriter Group => _group;

    internal BusGroupWriter(GroupWriter group, BusConfig busConfig)
    {
        _group = group;
        _timestampChannel = group.AddChannel<MeasTimestamp>("Timestamp");
        _rawChannel = group.AddRawChannel("RawFrames");

        BusDefinition = new BusChannelDefinition
        {
            BusConfig = busConfig,
            RawFrameChannelName = "RawFrames",
            TimestampChannelName = "Timestamp",
        };
    }

    /// <summary>Define a frame on this bus.</summary>
    /// <summary>Define a CAN frame.</summary>
    public CanFrameDefinition DefineCanFrame(string name, uint frameId, int payloadLength = 8,
        bool isExtendedId = false)
    {
        var frame = new CanFrameDefinition
        {
            Name = name, FrameId = frameId, PayloadLength = payloadLength,
            IsExtendedId = isExtendedId,
        };
        BusDefinition.Frames.Add(frame);
        return frame;
    }

    /// <summary>Define a CAN-FD frame.</summary>
    public CanFdFrameDefinition DefineCanFdFrame(string name, uint frameId, int payloadLength = 64,
        bool isExtendedId = false, bool bitRateSwitch = true)
    {
        var frame = new CanFdFrameDefinition
        {
            Name = name, FrameId = frameId, PayloadLength = payloadLength,
            IsExtendedId = isExtendedId, BitRateSwitch = bitRateSwitch,
        };
        BusDefinition.Frames.Add(frame);
        return frame;
    }

    /// <summary>Define a LIN frame.</summary>
    public LinFrameDefinition DefineLinFrame(string name, uint frameId, int payloadLength = 8)
    {
        var frame = new LinFrameDefinition
        {
            Name = name, FrameId = frameId, PayloadLength = payloadLength,
        };
        BusDefinition.Frames.Add(frame);
        return frame;
    }

    /// <summary>Define a FlexRay frame.</summary>
    public FlexRayFrameDefinition DefineFlexRayFrame(string name, uint slotId, int payloadLength,
        FlexRayChannel channel = FlexRayChannel.A)
    {
        var frame = new FlexRayFrameDefinition
        {
            Name = name, FrameId = slotId, PayloadLength = payloadLength,
            Channel = channel,
        };
        BusDefinition.Frames.Add(frame);
        return frame;
    }

    /// <summary>Define an Ethernet frame.</summary>
    public EthernetFrameDefinition DefineEthernetFrame(string name, uint frameId, int payloadLength,
        ushort etherType = 0)
    {
        var frame = new EthernetFrameDefinition
        {
            Name = name, FrameId = frameId, PayloadLength = payloadLength,
            EtherType = etherType,
        };
        BusDefinition.Frames.Add(frame);
        return frame;
    }

    /// <summary>Write a raw frame with standardized wire format for the bus type.</summary>
    public void WriteFrame(MeasTimestamp timestamp, uint frameId, ReadOnlySpan<byte> payload,
        FrameFlags flags = FrameFlags.None)
    {
        _timestampChannel.Write(timestamp);

        switch (BusDefinition.BusConfig.BusType)
        {
            case BusType.Can:
            case BusType.CanFd:
                WriteCanFrame(frameId, payload, flags);
                break;
            case BusType.Lin:
                WriteLinFrame(frameId, payload);
                break;
            case BusType.FlexRay:
                WriteFlexRayFrame(frameId, payload, flags);
                break;
            case BusType.Ethernet:
                WriteEthernetFrame(payload);
                break;
            default:
                WriteGenericFrame(frameId, payload);
                break;
        }
    }

    /// <summary>Write a raw frame with timestamp using pre-encoded bytes (no wire format encoding).</summary>
    public void WriteRawFrame(MeasTimestamp timestamp, ReadOnlySpan<byte> rawBytes)
    {
        _timestampChannel.Write(timestamp);
        _rawChannel.WriteFrame(rawBytes);
    }

    // CAN/CAN-FD: [uint32 arbId] [byte dlc] [byte flags] [payload]
    private void WriteCanFrame(uint arbId, ReadOnlySpan<byte> payload, FrameFlags flags)
    {
        Span<byte> frame = stackalloc byte[6 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, arbId);
        frame[4] = (byte)payload.Length;
        frame[5] = (byte)flags;
        payload.CopyTo(frame[6..]);
        _rawChannel.WriteFrame(frame);
    }

    // LIN: [byte frameId] [byte dlc] [byte nad] [byte checksumType] [payload]
    private void WriteLinFrame(uint frameId, ReadOnlySpan<byte> payload)
    {
        Span<byte> frame = stackalloc byte[4 + payload.Length];
        frame[0] = (byte)frameId;
        frame[1] = (byte)payload.Length;
        frame[2] = 0; // NAD - can be set via overload if needed
        frame[3] = 0; // checksum type
        payload.CopyTo(frame[4..]);
        _rawChannel.WriteFrame(frame);
    }

    // FlexRay: [uint16 slotId] [byte cycleCount] [byte channelFlags] [uint16 payloadLen] [payload]
    private void WriteFlexRayFrame(uint slotId, ReadOnlySpan<byte> payload, FrameFlags flags)
    {
        Span<byte> frame = stackalloc byte[6 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)slotId);
        frame[2] = 0; // cycle count
        frame[3] = (byte)flags;
        BinaryPrimitives.WriteUInt16LittleEndian(frame[4..], (ushort)payload.Length);
        payload.CopyTo(frame[6..]);
        _rawChannel.WriteFrame(frame);
    }

    // Ethernet: [6B macDst] [6B macSrc] [uint16 etherType] [uint16 vlanId] [uint16 payloadLen] [payload]
    private void WriteEthernetFrame(ReadOnlySpan<byte> payload)
    {
        Span<byte> frame = stackalloc byte[18 + payload.Length];
        frame[..18].Clear(); // MAC addresses + headers zeroed by default
        BinaryPrimitives.WriteUInt16LittleEndian(frame[16..], (ushort)payload.Length);
        payload.CopyTo(frame[18..]);
        _rawChannel.WriteFrame(frame);
    }

    // Generic: [uint32 frameId] [uint16 payloadLen] [payload]
    private void WriteGenericFrame(uint frameId, ReadOnlySpan<byte> payload)
    {
        Span<byte> frame = stackalloc byte[6 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, frameId);
        BinaryPrimitives.WriteUInt16LittleEndian(frame[4..], (ushort)payload.Length);
        payload.CopyTo(frame[6..]);
        _rawChannel.WriteFrame(frame);
    }
}

/// <summary>
/// Helper to extract frame ID and payload offset/length from raw wire format bytes.
/// </summary>
public static class BusFrameParser
{
    /// <summary>Get frame ID from raw wire bytes based on bus type.</summary>
    public static uint GetFrameId(byte[] raw, BusType busType) => busType switch
    {
        BusType.Can or BusType.CanFd => BinaryPrimitives.ReadUInt32LittleEndian(raw),
        BusType.Lin => raw[0],
        BusType.FlexRay => BinaryPrimitives.ReadUInt16LittleEndian(raw),
        _ => BinaryPrimitives.ReadUInt32LittleEndian(raw),
    };

    /// <summary>Get payload offset and length from raw wire bytes based on bus type.</summary>
    public static (int payloadOffset, int payloadLength) GetPayloadRange(byte[] raw, BusType busType) => busType switch
    {
        BusType.Can or BusType.CanFd => (6, raw[4]),       // skip arbId(4)+dlc(1)+flags(1)
        BusType.Lin => (4, raw[1]),                          // skip frameId(1)+dlc(1)+nad(1)+checksum(1)
        BusType.FlexRay => (6, BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(4))),
        _ => (6, BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(4))),
    };
}
