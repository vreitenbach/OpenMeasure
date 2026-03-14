namespace OpenMeasure.Bus;

public enum SignalDataType : byte
{
    Unsigned = 0,
    Signed = 1,
    IeeeFloat = 2,
    IeeeDouble = 3,
}

public sealed class SignalDefinition
{
    public required string Name { get; init; }
    public required int StartBit { get; init; }
    public required int BitLength { get; init; }
    public ByteOrder ByteOrder { get; init; } = ByteOrder.LittleEndian;
    public SignalDataType DataType { get; init; } = SignalDataType.Unsigned;

    // Linear conversion: physical = raw * Factor + Offset
    public double Factor { get; init; } = 1.0;
    public double Offset { get; init; } = 0.0;

    // Physical range
    public double? MinValue { get; init; }
    public double? MaxValue { get; init; }

    // Unit
    public string? Unit { get; init; }

    // Enum-style value descriptions
    public Dictionary<long, string>? ValueDescriptions { get; init; }

    // Multiplexing
    public bool IsMultiplexer { get; init; }
    public MultiplexCondition? MultiplexCondition { get; init; }
}
