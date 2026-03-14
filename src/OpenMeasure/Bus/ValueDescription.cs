namespace OpenMeasure.Bus;

/// <summary>
/// Named value table mapping raw integer values to descriptive strings.
/// Can be shared across multiple signals (like DBC value tables).
/// </summary>
public sealed class ValueTable
{
    public required string Name { get; init; }
    public required Dictionary<long, string> Entries { get; init; }

    public string? GetDescription(long rawValue) =>
        Entries.TryGetValue(rawValue, out var desc) ? desc : null;
}
