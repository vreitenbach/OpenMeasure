namespace OpenMeasure.Bus;

public enum BusType : byte
{
    None = 0,
    Can = 1,
    CanFd = 2,
    Lin = 3,
    FlexRay = 4,
    Ethernet = 5,
    Most = 6,
}

public abstract record BusConfig(BusType BusType);

public record CanBusConfig : BusConfig
{
    public bool IsExtendedId { get; init; }
    public int BaudRate { get; init; } = 500_000;
    public CanBusConfig() : base(BusType.Can) { }
}

public record CanFdBusConfig : BusConfig
{
    public bool IsExtendedId { get; init; }
    public int ArbitrationBaudRate { get; init; } = 500_000;
    public int DataBaudRate { get; init; } = 2_000_000;
    public CanFdBusConfig() : base(BusType.CanFd) { }
}

public record LinBusConfig : BusConfig
{
    public int BaudRate { get; init; } = 19_200;
    public byte LinVersion { get; init; } = 2;
    public LinBusConfig() : base(BusType.Lin) { }
}

public record FlexRayBusConfig : BusConfig
{
    public int CycleTimeUs { get; init; } = 5000;
    public int MacroticksPerCycle { get; init; }
    public FlexRayBusConfig() : base(BusType.FlexRay) { }
}

public record EthernetBusConfig : BusConfig
{
    public EthernetBusConfig() : base(BusType.Ethernet) { }
}

public record MostBusConfig : BusConfig
{
    public MostBusConfig() : base(BusType.Most) { }
}
