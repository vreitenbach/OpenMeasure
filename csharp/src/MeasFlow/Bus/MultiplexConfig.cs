namespace MeasFlow.Bus;

/// <summary>
/// Multiplexer configuration for a PDU or frame.
/// Defines which signals are active for each multiplexer value.
/// </summary>
public sealed class MultiplexConfig
{
    /// <summary>Name of the signal that acts as the multiplexer.</summary>
    public required string MultiplexerSignalName { get; init; }

    /// <summary>
    /// Signal groups keyed by multiplexer value.
    /// Each entry maps a MUX value to the list of signal names active for that value.
    /// </summary>
    public Dictionary<long, List<string>> MuxGroups { get; init; } = [];
}

/// <summary>
/// Condition under which a multiplexed signal is present.
/// Supports range matching and nested MUX (MUX within MUX).
/// </summary>
public sealed class MultiplexCondition
{
    /// <summary>Name of the multiplexer signal this condition references.</summary>
    public required string MultiplexerSignalName { get; init; }

    /// <summary>Signal is present when multiplexer value is in [LowValue, HighValue].</summary>
    public required long LowValue { get; init; }
    public required long HighValue { get; init; }

    /// <summary>For nested MUX: parent condition that must also be satisfied.</summary>
    public MultiplexCondition? ParentCondition { get; init; }
}
