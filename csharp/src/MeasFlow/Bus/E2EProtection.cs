namespace MeasFlow.Bus;

public enum E2EProfile : byte
{
    Profile01 = 1,
    Profile02 = 2,
    Profile04 = 4,
    Profile05 = 5,
    Profile06 = 6,
    Profile07 = 7,
    Profile11 = 11,
    ProfileJlr = 20,
}

/// <summary>
/// AUTOSAR E2E (End-to-End) protection configuration for a PDU.
/// Defines CRC and counter signal positions for data integrity verification.
/// </summary>
public sealed class E2EProtection
{
    public required E2EProfile Profile { get; init; }
    public required int CrcStartBit { get; init; }
    public required int CrcBitLength { get; init; }
    public required int CounterStartBit { get; init; }
    public required int CounterBitLength { get; init; }
    public uint DataId { get; init; }
    public uint CrcPolynomial { get; init; }
}
