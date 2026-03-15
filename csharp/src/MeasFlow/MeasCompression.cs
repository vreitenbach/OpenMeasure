namespace MeasFlow;

/// <summary>
/// Compression algorithm for data segments.
/// Stored in the lower 4 bits of SegmentHeader.Flags.
/// </summary>
public enum MeasCompression
{
    None = 0,
    Lz4 = 1,
    Zstd = 2,
}
