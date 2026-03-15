namespace MeasFlow.Bus;

[Flags]
public enum FrameFlags : ushort
{
    None        = 0,
    ErrorFrame  = 1 << 0,
    RemoteFrame = 1 << 1,
    FdFrame     = 1 << 2,
    BrsFlag     = 1 << 3,   // Bit Rate Switch (CAN-FD)
    EsiFlag     = 1 << 4,   // Error State Indicator (CAN-FD)
    WakeUp      = 1 << 5,   // LIN wakeup frame
    ChannelA    = 1 << 6,   // FlexRay channel A
    ChannelB    = 1 << 7,   // FlexRay channel B
}

public enum FrameDirection : byte
{
    Rx = 0,
    Tx = 1,
    TxRx = 2,
}

/// <summary>
/// Base class for all frame definitions. Common fields shared across bus types.
/// </summary>
public abstract class FrameDefinition
{
    public required string Name { get; init; }
    public required uint FrameId { get; init; }
    public required int PayloadLength { get; init; }
    public FrameDirection Direction { get; init; } = FrameDirection.Rx;
    public FrameFlags Flags { get; init; } = FrameFlags.None;

    /// <summary>PDUs within this frame. If empty, signals attach directly to the frame.</summary>
    public List<PduDefinition> Pdus { get; init; } = [];

    /// <summary>Signals attached directly to the frame (when no PDU layer).</summary>
    public List<SignalDefinition> Signals { get; init; } = [];
}

/// <summary>CAN 2.0A/B frame definition.</summary>
public sealed class CanFrameDefinition : FrameDefinition
{
    public bool IsExtendedId { get; init; }
}

/// <summary>CAN-FD frame definition.</summary>
public sealed class CanFdFrameDefinition : FrameDefinition
{
    public bool IsExtendedId { get; init; }
    public bool BitRateSwitch { get; init; }
    public bool ErrorStateIndicator { get; init; }
}

/// <summary>LIN frame definition.</summary>
public sealed class LinFrameDefinition : FrameDefinition
{
    public byte Nad { get; init; }
    public LinChecksumType ChecksumType { get; init; } = LinChecksumType.Enhanced;
}

public enum LinChecksumType : byte
{
    Classic = 0,
    Enhanced = 1,
}

/// <summary>FlexRay frame definition.</summary>
public sealed class FlexRayFrameDefinition : FrameDefinition
{
    /// <summary>FlexRay slot ID (= FrameId).</summary>
    public byte CycleCount { get; init; }
    public FlexRayChannel Channel { get; init; } = FlexRayChannel.A;
}

public enum FlexRayChannel : byte
{
    A = 0,
    B = 1,
    AB = 2,
}

/// <summary>Automotive Ethernet frame definition.</summary>
public sealed class EthernetFrameDefinition : FrameDefinition
{
    public byte[]? MacSource { get; init; }
    public byte[]? MacDestination { get; init; }
    public ushort VlanId { get; init; }
    public ushort EtherType { get; init; }
}

/// <summary>MOST frame definition.</summary>
public sealed class MostFrameDefinition : FrameDefinition
{
    public ushort FunctionBlock { get; init; }
    public byte InstanceId { get; init; }
    public ushort FunctionId { get; init; }
}
