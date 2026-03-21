using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MeasFlow.Bus;
using MeasFlow.Format;

namespace MeasFlow;

/// <summary>
/// Streaming writer for MEAS files. Supports incremental writing of measurement data.
/// </summary>
public sealed class MeasWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly List<GroupWriter> _groups = [];
    private readonly List<BusGroupWriter> _busGroups = [];
    private readonly Dictionary<string, MeasValue> _fileProperties = [];
    private FileHeader _header;
    private bool _metadataWritten;
    private bool _disposed;
    private long _segmentCount;
    private long _metadataSegmentOffset; // for re-patching stats on close
    private int _originalMetadataLength; // content length when first written, for repatch safety
    private Dictionary<string, MeasValue>? _frozenFileProperties; // snapshot taken at metadata-write time

    /// <summary>Compression algorithm applied to data segments.</summary>
    public MeasCompression Compression { get; set; }

    internal MeasWriter(FileStream stream)
    {
        _stream = stream;
        _header = FileHeader.Create();

        // Write initial header (will be updated on close)
        Span<byte> headerBuf = stackalloc byte[FileHeader.Size];
        _header.WriteTo(headerBuf);
        _stream.Write(headerBuf);
    }

    public Dictionary<string, MeasValue> Properties => _fileProperties;

    public GroupWriter AddGroup(string name)
    {
        if (_metadataWritten)
            throw new InvalidOperationException("Cannot add groups after data has been written.");
        var group = new GroupWriter(name, _groups.Count, this);
        _groups.Add(group);
        return group;
    }

    /// <summary>
    /// Add a bus data group with structured frame/signal definitions.
    /// </summary>
    public BusGroupWriter AddBusGroup(string name, BusConfig busConfig)
    {
        var group = AddGroup(name);
        var busGroup = new BusGroupWriter(group, busConfig);
        _busGroups.Add(busGroup);
        return busGroup;
    }

    /// <summary>
    /// Flush all buffered data to disk as a data segment.
    /// </summary>
    public void Flush()
    {
        EnsureMetadataWritten();

        // Collect all channels with pending data
        var pendingChunks = new List<(int globalIndex, ChannelWriter channel)>();
        foreach (var group in _groups)
        {
            foreach (var channel in group.ChannelWriters)
            {
                if (channel.HasPendingData)
                    pendingChunks.Add((channel.GlobalIndex, channel));
            }
        }

        if (pendingChunks.Count == 0)
            return;

        // Write data segment
        WriteDataSegment(pendingChunks);

        // Ensure bytes are visible to concurrent readers
        _stream.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Flush remaining data
        Flush();

        // Re-patch metadata segment with final statistics
        if (_metadataWritten)
            RepatchMetadataStatistics();

        // Update header with final segment count
        _header.SegmentCount = _segmentCount;
        _stream.Seek(0, SeekOrigin.Begin);
        Span<byte> headerBuf = stackalloc byte[FileHeader.Size];
        _header.WriteTo(headerBuf);
        _stream.Write(headerBuf);

        _stream.Flush();
        _stream.Dispose();

        // Return pooled buffers from all channels
        foreach (var group in _groups)
            foreach (var channel in group.ChannelWriters)
                channel.ReleaseResources();
    }

    private void EnsureMetadataWritten()
    {
        if (_metadataWritten) return;
        _metadataWritten = true;

        // Snapshot file properties so later mutations cannot cause size mismatches during repatch
        _frozenFileProperties = new Dictionary<string, MeasValue>(_fileProperties);

        // Always write extended metadata (version prefix + file properties)
        _header.Flags |= FileHeader.FlagExtendedMetadata;

        // Re-write header so concurrent readers see the correct flags
        _stream.Seek(0, SeekOrigin.Begin);
        Span<byte> headerBuf = stackalloc byte[FileHeader.Size];
        _header.WriteTo(headerBuf);
        _stream.Write(headerBuf);
        // Position is now at end of header = start of first segment

        // Serialize bus definitions into group properties before writing metadata
        foreach (var busGroup in _busGroups)
        {
            var encoded = BusMetadataEncoder.Encode(busGroup.BusDefinition);
            busGroup.Group.Properties["MEAS.bus_def"] = encoded;
        }

        WriteMetadataSegment();
    }

    private void WriteMetadataSegment()
    {
        var groups = new List<MeasGroupDefinition>();
        int globalIndex = 0;

        foreach (var group in _groups)
        {
            var channelDefs = new List<MeasChannelDefinition>();
            foreach (var ch in group.ChannelWriters)
            {
                ch.GlobalIndex = globalIndex++;
                var props = new Dictionary<string, MeasValue>(ch.Properties);
                ch.WriteStatistics(props);
                channelDefs.Add(new MeasChannelDefinition(ch.Name, ch.DataType, props));
            }
            groups.Add(new MeasGroupDefinition(
                group.Name,
                new Dictionary<string, MeasValue>(group.Properties),
                channelDefs));
        }

        var metadataBytes = MetadataEncoder.Encode(groups,
            fileProperties: _frozenFileProperties!, extended: true);

        _originalMetadataLength = metadataBytes.Length;

        var segHeader = new SegmentHeader
        {
            Type = SegmentType.Metadata,
            Flags = 0,
            ContentLength = metadataBytes.Length,
            NextSegmentOffset = 0, // will be patched
            ChunkCount = 0,
            Crc32 = 0,
        };

        long segStartOffset = _stream.Position;
        _metadataSegmentOffset = segStartOffset;
        Span<byte> segBuf = stackalloc byte[SegmentHeader.Size];
        segHeader.WriteTo(segBuf);
        _stream.Write(segBuf);
        _stream.Write(metadataBytes);

        // Patch NextSegmentOffset to point past this segment
        long nextOffset = _stream.Position;
        segHeader.NextSegmentOffset = nextOffset;
        segHeader.WriteTo(segBuf);
        _stream.Seek(segStartOffset, SeekOrigin.Begin);
        _stream.Write(segBuf);
        _stream.Seek(nextOffset, SeekOrigin.Begin);

        _segmentCount++;
    }

    /// <summary>
    /// Re-encode and overwrite the metadata segment with final statistics.
    /// Called from Dispose() after all data has been flushed.
    /// </summary>
    private void RepatchMetadataStatistics()
    {
        // Re-encode metadata with final statistics
        var groups = new List<MeasGroupDefinition>();
        int globalIndex = 0;

        foreach (var group in _groups)
        {
            var channelDefs = new List<MeasChannelDefinition>();
            foreach (var ch in group.ChannelWriters)
            {
                ch.GlobalIndex = globalIndex++;
                var props = new Dictionary<string, MeasValue>(ch.Properties);
                ch.WriteStatistics(props);
                channelDefs.Add(new MeasChannelDefinition(ch.Name, ch.DataType, props));
            }
            groups.Add(new MeasGroupDefinition(
                group.Name,
                new Dictionary<string, MeasValue>(group.Properties),
                channelDefs));
        }

        var metadataBytes = MetadataEncoder.Encode(groups,
            fileProperties: _frozenFileProperties!, extended: true);

        if (metadataBytes.Length != _originalMetadataLength)
            throw new InvalidOperationException(
                $"Re-encoded metadata size ({metadataBytes.Length} bytes) differs from the original " +
                $"({_originalMetadataLength} bytes). This would corrupt the file. " +
                "Ensure file properties are not mutated after the first write.");

        // Seek to metadata segment and overwrite
        _stream.Seek(_metadataSegmentOffset, SeekOrigin.Begin);

        var segHeader = new SegmentHeader
        {
            Type = SegmentType.Metadata,
            Flags = 0,
            ContentLength = metadataBytes.Length,
            NextSegmentOffset = _metadataSegmentOffset + SegmentHeader.Size + metadataBytes.Length,
            ChunkCount = 0,
            Crc32 = 0,
        };

        Span<byte> segBuf = stackalloc byte[SegmentHeader.Size];
        segHeader.WriteTo(segBuf);
        _stream.Write(segBuf);
        _stream.Write(metadataBytes);

        // Seek back to end of file
        _stream.Seek(0, SeekOrigin.End);
    }

    private void WriteDataSegment(List<(int globalIndex, ChannelWriter channel)> chunks)
    {
        bool compressed = Compression != MeasCompression.None;

        if (compressed)
        {
            // Compression requires knowing full content → buffer first
            WriteDataSegmentBuffered(chunks);
            return;
        }

        // Fast path: write directly to file stream (no intermediate buffer)
        var segHeader = new SegmentHeader
        {
            Type = SegmentType.Data,
            Flags = 0,
            ContentLength = 0, // patched after writing
            NextSegmentOffset = 0,
            ChunkCount = chunks.Count,
            Crc32 = 0,
        };

        long segStartOffset = _stream.Position;
        Span<byte> segBuf = stackalloc byte[SegmentHeader.Size];
        segHeader.WriteTo(segBuf);
        _stream.Write(segBuf); // placeholder header

        // Write chunk count + channel data directly to file
        Span<byte> intBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(intBuf, chunks.Count);
        _stream.Write(intBuf);

        foreach (var (globalIndex, channel) in chunks)
        {
            channel.FlushToStream(_stream, globalIndex);
        }

        // Patch header with actual content length and next offset
        long nextOffset = _stream.Position;
        segHeader.ContentLength = nextOffset - segStartOffset - SegmentHeader.Size;
        segHeader.NextSegmentOffset = nextOffset;
        segHeader.WriteTo(segBuf);
        _stream.Seek(segStartOffset, SeekOrigin.Begin);
        _stream.Write(segBuf);
        _stream.Seek(nextOffset, SeekOrigin.Begin);

        _segmentCount++;
    }

    private void WriteDataSegmentBuffered(List<(int globalIndex, ChannelWriter channel)> chunks)
    {
        using var dataBuffer = new MemoryStream();

        Span<byte> intBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(intBuf, chunks.Count);
        dataBuffer.Write(intBuf);

        foreach (var (globalIndex, channel) in chunks)
        {
            channel.FlushToStream(dataBuffer, globalIndex);
        }

        // Use GetBuffer() to avoid ToArray() copy when possible
        byte[] contentData = SegmentCompressor.Compress(
            dataBuffer.GetBuffer().AsSpan(0, (int)dataBuffer.Length), Compression);

        var segHeader = new SegmentHeader
        {
            Type = SegmentType.Data,
            Flags = SegmentCompressor.ToFlags(Compression),
            ContentLength = contentData.Length,
            NextSegmentOffset = 0,
            ChunkCount = chunks.Count,
            Crc32 = 0,
        };

        long segStartOffset = _stream.Position;
        Span<byte> segBuf = stackalloc byte[SegmentHeader.Size];
        segHeader.WriteTo(segBuf);
        _stream.Write(segBuf);
        _stream.Write(contentData);

        long nextOffset = _stream.Position;
        segHeader.NextSegmentOffset = nextOffset;
        segHeader.WriteTo(segBuf);
        _stream.Seek(segStartOffset, SeekOrigin.Begin);
        _stream.Write(segBuf);
        _stream.Seek(nextOffset, SeekOrigin.Begin);

        _segmentCount++;
    }
}

/// <summary>
/// Writer for a single group within an MEAS file.
/// </summary>
public sealed class GroupWriter
{
    public string Name { get; }
    public Dictionary<string, MeasValue> Properties { get; } = [];
    internal List<ChannelWriter> ChannelWriters { get; } = [];
    private readonly int _groupIndex;
    private readonly MeasWriter _writer;

    internal GroupWriter(string name, int groupIndex, MeasWriter writer)
    {
        Name = name;
        _groupIndex = groupIndex;
        _writer = writer;
    }

    public ChannelWriter<T> AddChannel<T>(string name, bool trackStatistics = true) where T : unmanaged
    {
        var dataType = MeasDataTypeExtensions.FromClrType<T>();
        var channel = new ChannelWriter<T>(name, dataType, trackStatistics);
        ChannelWriters.Add(channel);
        return channel;
    }

    /// <summary>
    /// Add a raw binary channel for variable-length frames (CAN, LIN, etc.).
    /// </summary>
    public RawChannelWriter AddRawChannel(string name)
    {
        var channel = new RawChannelWriter(name);
        ChannelWriters.Add(channel);
        return channel;
    }

    /// <summary>
    /// Add a signal channel that is decoded from a raw source channel.
    /// The source relationship is stored as a property for traceability.
    /// </summary>
    [Obsolete("Use MeasWriter.AddBusGroup() with structured frame/signal definitions instead.")]
    public ChannelWriter<T> AddSignalChannel<T>(string name, string sourceChannelName,
        int startBit = -1, int bitLength = -1, double factor = 1.0, double offset = 0.0)
        where T : unmanaged
    {
        var channel = AddChannel<T>(name);
        channel.Properties["meas.source_channel"] = sourceChannelName;
        if (startBit >= 0) channel.Properties["meas.start_bit"] = startBit;
        if (bitLength >= 0) channel.Properties["meas.bit_length"] = bitLength;
        if (factor != 1.0) channel.Properties["meas.factor"] = factor;
        if (offset != 0.0) channel.Properties["meas.offset"] = offset;
        return channel;
    }
}

/// <summary>
/// Base class for channel writers.
/// </summary>
public abstract class ChannelWriter
{
    public string Name { get; }
    public MeasDataType DataType { get; }
    public Dictionary<string, MeasValue> Properties { get; } = [];
    internal int GlobalIndex { get; set; }
    internal abstract bool HasPendingData { get; }
    internal abstract void FlushToStream(Stream stream, int globalIndex);

    protected ChannelWriter(string name, MeasDataType dataType)
    {
        Name = name;
        DataType = dataType;
    }

    /// <summary>Write statistics to properties dictionary. Override in typed writers.</summary>
    internal virtual void WriteStatistics(Dictionary<string, MeasValue> props) { }

    /// <summary>Return any pooled resources. Called from MeasWriter.Dispose.</summary>
    internal virtual void ReleaseResources() { }
}

/// <summary>
/// Typed channel writer for fixed-size data types.
/// Uses ArrayPool to minimize allocations and GC pressure.
/// </summary>
public sealed class ChannelWriter<T> : ChannelWriter where T : unmanaged
{
    private byte[]? _buffer;
    private int _byteCount;
    private int _sampleCount;
    private readonly bool _trackStats;
    private StatisticsAccumulator _stats;

    internal ChannelWriter(string name, MeasDataType dataType, bool trackStatistics = true)
        : base(name, dataType)
    {
        _trackStats = trackStatistics && NumericConverter.IsNumeric<T>();
    }

    internal override bool HasPendingData => _sampleCount > 0;

    /// <summary>Current running statistics for this channel.</summary>
    public ChannelStatistics Statistics => _stats.ToStatistics();

    public void Write(T value)
    {
        int size = Unsafe.SizeOf<T>();
        EnsureCapacity(_byteCount + size);
        Unsafe.WriteUnaligned(ref _buffer![_byteCount], value);
        _byteCount += size;
        _sampleCount++;
        if (_trackStats) _stats.Update(NumericConverter.ToDouble(value));
    }

    /// <summary>Write a batch of samples from a span.</summary>
    public void Write(ReadOnlySpan<T> values)
    {
        if (values.IsEmpty) return;

        // Direct byte copy — no intermediate List<T>
        var bytes = MemoryMarshal.AsBytes(values);
        EnsureCapacity(_byteCount + bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_byteCount));
        _byteCount += bytes.Length;
        _sampleCount += values.Length;

        if (_trackStats)
            _stats.UpdateBulk(values);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int needed)
    {
        if (_buffer == null)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(needed, 4096));
            return;
        }
        if (needed <= _buffer.Length) return;
        int newSize = Math.Max(needed, _buffer.Length * 2);
        var newBuf = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _byteCount).CopyTo(newBuf);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuf;
    }

    internal override void WriteStatistics(Dictionary<string, MeasValue> props)
    {
        if (_trackStats) _stats.ToStatistics().WriteToProperties(props);
    }

    internal override void FlushToStream(Stream stream, int globalIndex)
    {
        if (_sampleCount == 0) return;

        DataEncoder.WriteChunkHeader(stream, globalIndex, _sampleCount, _byteCount);
        stream.Write(_buffer.AsSpan(0, _byteCount));

        // Return large buffer to pool, start fresh on next write
        ArrayPool<byte>.Shared.Return(_buffer!);
        _buffer = null;
        _byteCount = 0;
        _sampleCount = 0;
    }

    internal override void ReleaseResources()
    {
        if (_buffer != null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }
    }
}

/// <summary>
/// Channel writer for variable-length binary frames (CAN/LIN messages, raw bus data).
/// </summary>
public sealed class RawChannelWriter : ChannelWriter
{
    private readonly List<byte[]> _frames = [];

    internal RawChannelWriter(string name) : base(name, MeasDataType.Binary) { }

    internal override bool HasPendingData => _frames.Count > 0;

    /// <summary>Write a single raw frame (e.g., one CAN message).</summary>
    public void WriteFrame(ReadOnlySpan<byte> frame) => _frames.Add(frame.ToArray());

    /// <summary>Write a single raw frame from a byte array.</summary>
    public void WriteFrame(byte[] frame) => _frames.Add(frame);

    internal override void FlushToStream(Stream stream, int globalIndex)
    {
        if (_frames.Count == 0) return;

        // Pre-calculate total byte size
        long totalDataBytes = 0;
        foreach (var frame in _frames)
            totalDataBytes += 4 + frame.Length; // int32 length prefix + data

        DataEncoder.WriteChunkHeader(stream, globalIndex, _frames.Count, totalDataBytes);

        Span<byte> lenBuf = stackalloc byte[4];
        foreach (var frame in _frames)
        {
            BinaryPrimitives.WriteInt32LittleEndian(lenBuf, frame.Length);
            stream.Write(lenBuf);
            stream.Write(frame);
        }

        _frames.Clear();
    }
}
