namespace MeasFlow.Bus;

/// <summary>
/// AUTOSAR SecOC (Secure Onboard Communication) configuration for a PDU.
///
/// SecOC provides authentication of PDUs via a truncated MAC (Message Authentication Code)
/// and a freshness value (counter or timestamp-based) to prevent replay attacks.
///
/// The Secured I-PDU layout:
///   [Authentic I-PDU payload] [Freshness Value (truncated)] [MAC (truncated)]
/// </summary>
public sealed class SecOcConfig
{
    /// <summary>SecOC authentication algorithm.</summary>
    public required SecOcAlgorithm Algorithm { get; init; }

    // --- Freshness Value ---

    /// <summary>Bit position of the truncated freshness value within the Secured I-PDU.</summary>
    public required int FreshnessValueStartBit { get; init; }

    /// <summary>Bit length of the truncated freshness value transmitted in the PDU.</summary>
    public required int FreshnessValueTruncatedLength { get; init; }

    /// <summary>Full length of the freshness value used for MAC computation (before truncation).</summary>
    public int FreshnessValueFullLength { get; init; } = 64;

    /// <summary>Freshness value source type.</summary>
    public FreshnessValueType FreshnessType { get; init; } = FreshnessValueType.Counter;

    // --- MAC (Message Authentication Code) ---

    /// <summary>Bit position of the truncated MAC within the Secured I-PDU.</summary>
    public required int MacStartBit { get; init; }

    /// <summary>Bit length of the truncated MAC transmitted in the PDU.</summary>
    public required int MacTruncatedLength { get; init; }

    /// <summary>Full length of the MAC before truncation (algorithm-dependent).</summary>
    public int MacFullLength { get; init; } = 128;

    // --- Authentication ---

    /// <summary>Length of the authentic payload in bits (the data being protected).</summary>
    public required int AuthenticPayloadLength { get; init; }

    /// <summary>
    /// Data ID for the SecOC PDU. Used as additional input to the MAC algorithm
    /// to bind the MAC to a specific PDU definition.
    /// </summary>
    public uint DataId { get; init; }

    /// <summary>
    /// Authentication Build Counter — number of attempts allowed to verify
    /// the freshness value before declaring authentication failure.
    /// </summary>
    public int AuthenticationBuildAttempts { get; init; } = 1;

    /// <summary>
    /// If true, the freshness value is managed by an external Freshness Manager (FVM).
    /// If false, a simple counter is used.
    /// </summary>
    public bool UseFreshnessValueManager { get; init; }

    /// <summary>
    /// Key ID referencing the cryptographic key used for MAC computation.
    /// Maps to a key in the SecOC key management system.
    /// </summary>
    public uint KeyId { get; init; }
}

public enum SecOcAlgorithm : byte
{
    /// <summary>CMAC with AES-128 (most common in automotive).</summary>
    CmacAes128 = 0,

    /// <summary>HMAC with SHA-256.</summary>
    HmacSha256 = 1,

    /// <summary>CMAC with AES-256.</summary>
    CmacAes256 = 2,

    /// <summary>HMAC with SHA-384.</summary>
    HmacSha384 = 3,
}

public enum FreshnessValueType : byte
{
    /// <summary>Simple monotonic counter.</summary>
    Counter = 0,

    /// <summary>Timestamp-based freshness (synchronized time).</summary>
    Timestamp = 1,

    /// <summary>Counter + timestamp combination.</summary>
    CounterAndTimestamp = 2,
}
