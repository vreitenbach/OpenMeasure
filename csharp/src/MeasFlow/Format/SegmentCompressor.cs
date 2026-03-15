using K4os.Compression.LZ4;
using ZstdSharp;

namespace MeasFlow.Format;

internal static class SegmentCompressor
{
    private const int CompressionMask = 0x0F;

    public static int ToFlags(MeasCompression compression) => (int)compression & CompressionMask;

    public static MeasCompression FromFlags(int flags) => (MeasCompression)(flags & CompressionMask);

    public static byte[] Compress(byte[] data, MeasCompression compression) => compression switch
    {
        MeasCompression.Lz4 => CompressLz4(data),
        MeasCompression.Zstd => CompressZstd(data),
        _ => data,
    };

    public static byte[] Decompress(byte[] data, MeasCompression compression) => compression switch
    {
        MeasCompression.Lz4 => DecompressLz4(data),
        MeasCompression.Zstd => DecompressZstd(data),
        _ => data,
    };

    private static byte[] CompressLz4(byte[] data)
    {
        int maxLen = LZ4Codec.MaximumOutputSize(data.Length);
        var target = new byte[4 + maxLen]; // 4 bytes for original size prefix
        BitConverter.TryWriteBytes(target.AsSpan(0, 4), data.Length);
        int written = LZ4Codec.Encode(data, target.AsSpan(4));
        return target.AsSpan(0, 4 + written).ToArray();
    }

    private static byte[] DecompressLz4(byte[] data)
    {
        int originalSize = BitConverter.ToInt32(data, 0);
        var target = new byte[originalSize];
        LZ4Codec.Decode(data.AsSpan(4), target);
        return target;
    }

    private static byte[] CompressZstd(byte[] data)
    {
        using var compressor = new Compressor(3); // level 3 = good balance
        var compressed = compressor.Wrap(data);
        return compressed.ToArray();
    }

    private static byte[] DecompressZstd(byte[] data)
    {
        using var decompressor = new Decompressor();
        var decompressed = decompressor.Unwrap(data);
        return decompressed.ToArray();
    }
}
