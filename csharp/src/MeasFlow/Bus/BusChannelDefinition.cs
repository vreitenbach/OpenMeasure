namespace MeasFlow.Bus;

/// <summary>
/// Complete bus channel definition: bus configuration + all frame/PDU/signal definitions.
/// Stored as structured binary metadata on a group via BusMetadataEncoder.
/// </summary>
public sealed class BusChannelDefinition
{
    public required BusConfig BusConfig { get; init; }
    public required string RawFrameChannelName { get; init; }
    public required string TimestampChannelName { get; init; }

    /// <summary>All frame definitions on this bus.</summary>
    public List<FrameDefinition> Frames { get; init; } = [];

    /// <summary>Shared value tables referenceable by name from signals.</summary>
    public List<ValueTable> ValueTables { get; init; } = [];

    public FrameDefinition? FindFrame(uint frameId) =>
        Frames.FirstOrDefault(f => f.FrameId == frameId);

    public FrameDefinition? FindFrame(string name) =>
        Frames.FirstOrDefault(f => f.Name == name);

    /// <summary>Flatten all signals across all frames and PDUs.</summary>
    public IEnumerable<(FrameDefinition Frame, PduDefinition? Pdu, SignalDefinition Signal)> AllSignals()
    {
        foreach (var frame in Frames)
        {
            foreach (var signal in frame.Signals)
                yield return (frame, null, signal);
            foreach (var pdu in frame.Pdus)
            {
                foreach (var signal in pdu.Signals)
                    yield return (frame, pdu, signal);
                foreach (var contained in pdu.ContainedPdus)
                    foreach (var signal in contained.Signals)
                        yield return (frame, pdu, signal);
            }
        }
    }

    /// <summary>Find a signal by name across all frames/PDUs.</summary>
    public (FrameDefinition Frame, PduDefinition? Pdu, SignalDefinition Signal)? FindSignal(string signalName)
    {
        foreach (var entry in AllSignals())
        {
            if (entry.Signal.Name == signalName)
                return entry;
        }
        return null;
    }
}
