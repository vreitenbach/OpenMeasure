using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace MeasFlow.Format;

/// <summary>
/// 64-byte file header at the start of every .meas file.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FileHeader
{
    public const int Size = 64;
    public const uint MagicNumber = 0x5341454D; // "MEAS" in little-endian (0x4D 0x45 0x41 0x53)
    public const ushort FlagExtendedMetadata = 0x0001;

    public uint Magic;           // 4  "MEAS"
    public ushort Version;       // 2  Format version (1)
    public ushort Flags;         // 2  Bit flags
    public long FirstSegmentOffset; // 8  Offset to first segment
    public long IndexOffset;     // 8  Offset to index segment (0 = none)
    public long SegmentCount;    // 8  Total segments written
    public Guid FileId;          // 16 Unique file identifier
    public long CreatedAtNanos;  // 8  MeasTimestamp (nanos since epoch)
    public long Reserved;        // 8  Padding to 64 bytes

    public static FileHeader Create()
    {
        return new FileHeader
        {
            Magic = MagicNumber,
            Version = 1,
            Flags = 0,
            FirstSegmentOffset = Size, // immediately after header
            IndexOffset = 0,
            SegmentCount = 0,
            FileId = Guid.NewGuid(),
            CreatedAtNanos = MeasTimestamp.Now.Nanoseconds,
            Reserved = 0,
        };
    }

    public void WriteTo(Span<byte> buffer)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0..], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..], Flags);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[8..], FirstSegmentOffset);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[16..], IndexOffset);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[24..], SegmentCount);
        FileId.TryWriteBytes(buffer[32..]);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[48..], CreatedAtNanos);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[56..], Reserved);
    }

    public static FileHeader ReadFrom(ReadOnlySpan<byte> buffer)
    {
        var header = new FileHeader
        {
            Magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0..]),
            Version = BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..]),
            Flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..]),
            FirstSegmentOffset = BinaryPrimitives.ReadInt64LittleEndian(buffer[8..]),
            IndexOffset = BinaryPrimitives.ReadInt64LittleEndian(buffer[16..]),
            SegmentCount = BinaryPrimitives.ReadInt64LittleEndian(buffer[24..]),
            FileId = new Guid(buffer[32..48]),
            CreatedAtNanos = BinaryPrimitives.ReadInt64LittleEndian(buffer[48..]),
            Reserved = BinaryPrimitives.ReadInt64LittleEndian(buffer[56..]),
        };

        if (header.Magic != MagicNumber)
            throw new InvalidDataException($"Invalid .meas file (magic=0x{header.Magic:X8}, expected 0x{MagicNumber:X8})");

        return header;
    }
}
