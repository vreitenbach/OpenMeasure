using System.Runtime.InteropServices;

namespace OpenMeasure;

/// <summary>
/// A single data channel (read-side). Provides typed access to measurement data.
/// </summary>
public sealed class OmxChannel
{
    public string Name { get; }
    public OmxDataType DataType { get; }
    public long SampleCount { get; internal set; }
    public IReadOnlyDictionary<string, OmxValue> Properties => _properties;

    /// <summary>
    /// Name of the raw source channel this signal was decoded from (e.g., CAN frame channel).
    /// Null if this channel contains raw/independent data.
    /// </summary>
    public string? SourceChannelName => _properties.TryGetValue("omx.source_channel", out var v) ? v.AsString() : null;

    /// <summary>
    /// The group-relative index of this channel.
    /// </summary>
    internal int Index { get; }

    private readonly Dictionary<string, OmxValue> _properties;
    private readonly List<DataChunkRef> _chunks;

    internal OmxChannel(string name, OmxDataType dataType, int index, Dictionary<string, OmxValue> properties)
    {
        Name = name;
        DataType = dataType;
        Index = index;
        _properties = properties;
        _chunks = [];
    }

    internal void AddChunk(DataChunkRef chunk)
    {
        _chunks.Add(chunk);
        SampleCount += chunk.SampleCount;
    }

    /// <summary>Read all samples as a typed array.</summary>
    public T[] ReadAll<T>() where T : unmanaged
    {
        ValidateType<T>();
        int totalSamples = checked((int)SampleCount);
        var result = new T[totalSamples];
        int destOffset = 0;

        foreach (var chunk in _chunks)
        {
            int count = (int)chunk.SampleCount;
            var sourceBytes = chunk.GetBytes();
            var sourceSpan = MemoryMarshal.Cast<byte, T>(sourceBytes);
            sourceSpan[..count].CopyTo(result.AsSpan(destOffset));
            destOffset += count;
        }

        return result;
    }

    /// <summary>Read all samples as a Span (only if single contiguous chunk, otherwise throws).</summary>
    public ReadOnlySpan<T> ReadSpan<T>() where T : unmanaged
    {
        ValidateType<T>();
        if (_chunks.Count == 1)
        {
            var bytes = _chunks[0].GetBytes();
            return MemoryMarshal.Cast<byte, T>(bytes)[..(int)_chunks[0].SampleCount];
        }
        throw new InvalidOperationException(
            $"Channel '{Name}' has {_chunks.Count} chunks. Use ReadAll<T>() for multi-chunk channels.");
    }

    /// <summary>Read variable-length binary frames (CAN/LIN messages etc.).</summary>
    public List<byte[]> ReadFrames()
    {
        if (DataType != OmxDataType.Binary)
            throw new InvalidOperationException($"Channel '{Name}' is {DataType}, not Binary.");

        var result = new List<byte[]>();
        foreach (var chunk in _chunks)
        {
            var bytes = chunk.GetBytes();
            int offset = 0;
            while (offset < bytes.Length)
            {
                int frameLen = BitConverter.ToInt32(bytes[offset..]);
                offset += 4;
                var frame = bytes[offset..(offset + frameLen)].ToArray();
                result.Add(frame);
                offset += frameLen;
            }
        }
        return result;
    }

    /// <summary>Enumerate chunks for streaming reads.</summary>
    public IEnumerable<ReadOnlyMemory<T>> ReadChunks<T>() where T : unmanaged
    {
        ValidateType<T>();
        foreach (var chunk in _chunks)
        {
            var bytes = chunk.GetBytes().ToArray(); // copy needed for Memory<T>
            var memory = new Memory<byte>(bytes);
            var count = (int)chunk.SampleCount;
            yield return MemoryMarshal.Cast<byte, T>(memory.Span)[..count].ToArray();
        }
    }

    private void ValidateType<T>() where T : unmanaged
    {
        var expected = OmxDataTypeExtensions.FromClrType<T>();
        if (expected != DataType)
            throw new InvalidCastException(
                $"Channel '{Name}' is {DataType} but requested as {expected} ({typeof(T).Name}).");
    }
}

/// <summary>
/// Reference to a chunk of raw data bytes within the file.
/// </summary>
internal readonly struct DataChunkRef
{
    public readonly long SampleCount;
    private readonly byte[] _data;

    public DataChunkRef(long sampleCount, byte[] data)
    {
        SampleCount = sampleCount;
        _data = data;
    }

    public ReadOnlySpan<byte> GetBytes() => _data;
}
