namespace OpenMeasure.Bus;

public sealed class PduDefinition
{
    public required string Name { get; init; }
    public required uint PduId { get; init; }
    public required int ByteOffset { get; init; }
    public required int Length { get; init; }

    /// <summary>Signals within this PDU.</summary>
    public List<SignalDefinition> Signals { get; init; } = [];

    /// <summary>Multiplexing configuration for this PDU.</summary>
    public MultiplexConfig? Multiplexing { get; init; }

    /// <summary>If true, this is an AUTOSAR Container PDU (I-PDU Multiplexing).</summary>
    public bool IsContainerPdu { get; init; }

    /// <summary>Contained PDUs when IsContainerPdu is true.</summary>
    public List<ContainedPduDefinition> ContainedPdus { get; init; } = [];

    /// <summary>E2E protection applied to this PDU.</summary>
    public E2EProtection? E2EProtection { get; init; }

    /// <summary>SecOC protection applied to this PDU.</summary>
    public SecOcConfig? SecOc { get; init; }
}

public sealed class ContainedPduDefinition
{
    public required string Name { get; init; }
    public required uint HeaderId { get; init; }
    public required int Length { get; init; }

    /// <summary>Signals within this contained PDU.</summary>
    public List<SignalDefinition> Signals { get; init; } = [];
}
