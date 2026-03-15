using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace MeasFlow.Format;

/// <summary>
/// Encodes/decodes data segments containing channel data chunks.
///
/// Data segment wire format:
///   [int32: chunkCount]
///   for each chunk:
///     [int32: channelIndex] [int64: sampleCount] [int64: dataByteLength] [raw bytes...]
///
/// For fixed-size types, raw bytes = sampleCount * sizeof(T), little-endian.
/// For variable-size types (strings, binary/raw frames):
///   each element: [int32: byteLength] [bytes...]
/// </summary>
internal static class DataEncoder
{
    public static void WriteChunkHeader(Stream stream, int channelIndex, long sampleCount, long dataByteLength)
    {
        Span<byte> buf = stackalloc byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(buf[0..], channelIndex);
        BinaryPrimitives.WriteInt64LittleEndian(buf[4..], sampleCount);
        BinaryPrimitives.WriteInt64LittleEndian(buf[12..], dataByteLength);
        stream.Write(buf);
    }

    public static (int channelIndex, long sampleCount, long dataByteLength) ReadChunkHeader(ReadOnlySpan<byte> data, ref int offset)
    {
        int channelIndex = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        offset += 4;
        long sampleCount = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
        offset += 8;
        long dataByteLength = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
        offset += 8;
        return (channelIndex, sampleCount, dataByteLength);
    }

    public const int ChunkHeaderSize = 4 + 8 + 8; // 20 bytes

    /// <summary>
    /// Writes a span of unmanaged values directly as bytes (zero-copy for little-endian platforms).
    /// </summary>
    public static void WriteFixedData<T>(Stream stream, ReadOnlySpan<T> data) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(data);
        stream.Write(bytes);
    }

    /// <summary>
    /// Writes variable-length binary frames (CAN messages, etc.).
    /// Each frame: [int32: length] [bytes]
    /// </summary>
    public static long WriteVariableData(Stream stream, IReadOnlyList<ReadOnlyMemory<byte>> frames)
    {
        long totalBytes = 0;
        Span<byte> lenBuf = stackalloc byte[4];

        foreach (var frame in frames)
        {
            BinaryPrimitives.WriteInt32LittleEndian(lenBuf, frame.Length);
            stream.Write(lenBuf);
            stream.Write(frame.Span);
            totalBytes += 4 + frame.Length;
        }

        return totalBytes;
    }
}
