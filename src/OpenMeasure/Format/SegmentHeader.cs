using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace OpenMeasure.Format;

internal enum SegmentType : int
{
    Metadata = 1,
    Data = 2,
    Index = 3,
}

/// <summary>
/// 32-byte segment header preceding every segment.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SegmentHeader
{
    public const int Size = 32;

    public SegmentType Type;         // 4
    public int Flags;                // 4  (compression, etc.)
    public long ContentLength;       // 8  bytes of content after this header
    public long NextSegmentOffset;   // 8  absolute offset (0 = last segment)
    public int ChunkCount;           // 4  number of channel chunks in this segment
    public uint Crc32;               // 4  CRC32 of segment content

    public void WriteTo(Span<byte> buffer)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer[0..], (int)Type);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[4..], Flags);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[8..], ContentLength);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[16..], NextSegmentOffset);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[24..], ChunkCount);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[28..], Crc32);
    }

    public static SegmentHeader ReadFrom(ReadOnlySpan<byte> buffer)
    {
        return new SegmentHeader
        {
            Type = (SegmentType)BinaryPrimitives.ReadInt32LittleEndian(buffer[0..]),
            Flags = BinaryPrimitives.ReadInt32LittleEndian(buffer[4..]),
            ContentLength = BinaryPrimitives.ReadInt64LittleEndian(buffer[8..]),
            NextSegmentOffset = BinaryPrimitives.ReadInt64LittleEndian(buffer[16..]),
            ChunkCount = BinaryPrimitives.ReadInt32LittleEndian(buffer[24..]),
            Crc32 = BinaryPrimitives.ReadUInt32LittleEndian(buffer[28..]),
        };
    }
}
