using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using MeasFlow.Format;
using static MeasFlow.Format.SegmentCompressor;

namespace MeasFlow;

/// <summary>
/// Reader for MEAS files. Uses memory-mapped I/O for efficient access to large files.
/// </summary>
public sealed class MeasReader : IDisposable
{
    internal FileHeader Header { get; private set; }
    public IReadOnlyList<MeasGroup> Groups => _groups;
    public IReadOnlyDictionary<string, MeasValue> Properties => _fileProperties;
    public MeasTimestamp CreatedAt => new(Header.CreatedAtNanos);

    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _accessor;
    private readonly List<MeasGroup> _groups = [];
    private readonly Dictionary<string, MeasGroup> _groupsByName = [];
    private readonly Dictionary<string, MeasValue> _fileProperties = [];
    private readonly List<MeasChannel> _allChannels = [];
    private bool _disposed;
    private unsafe byte* _mmfBasePtr;
    private bool _ptrAcquired;

    /// <summary>Base pointer into the memory-mapped file (null if not available).</summary>
    internal unsafe byte* MmfBasePointer => _mmfBasePtr;

    private unsafe MeasReader(MemoryMappedFile mmf, MemoryMappedViewAccessor accessor)
    {
        _mmf = mmf;
        _accessor = accessor;
        // Acquire pointer once for the lifetime of the reader
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _mmfBasePtr);
        _mmfBasePtr += accessor.PointerOffset;
        _ptrAcquired = true;
    }

    /// <summary>
    /// Open a .meas file using memory-mapped I/O. Supports concurrent reads while
    /// another process is writing (the file is mapped read-only with FileShare.ReadWrite).
    /// </summary>
    public static MeasReader Open(string path)
    {
        var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long fileLength = fileStream.Length;

        if (fileLength == 0)
        {
            fileStream.Dispose();
            throw new InvalidDataException("File is empty.");
        }

        var mmf = MemoryMappedFile.CreateFromFile(fileStream, null, 0,
            MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
        var accessor = mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);

        var reader = new MeasReader(mmf, accessor);
        reader.ReadFile(fileLength);
        return reader;
    }

    public MeasGroup this[string groupName] =>
        _groupsByName.TryGetValue(groupName, out var g)
            ? g
            : throw new KeyNotFoundException($"Group '{groupName}' not found.");

    public bool TryGetGroup(string name, out MeasGroup? group)
        => _groupsByName.TryGetValue(name, out group);

    private void ReadFile(long fileLength)
    {
        Span<byte> headerBuf = stackalloc byte[FileHeader.Size];
        ReadBytes(0, headerBuf);
        Header = FileHeader.ReadFrom(headerBuf);

        // Walk segments using the forward-linked segment chain.
        // When SegmentCount > 0 (file closed normally), use it as the upper bound.
        // When SegmentCount == 0 (writer still open / streaming), walk until
        // we run out of readable data — this enables concurrent read while writing.
        long maxSegments = Header.SegmentCount > 0 ? Header.SegmentCount : long.MaxValue;
        long offset = Header.FirstSegmentOffset;

        Span<byte> segBuf = stackalloc byte[SegmentHeader.Size];
        for (long i = 0; i < maxSegments; i++)
        {
            if (offset + SegmentHeader.Size > fileLength)
                break;

            ReadBytes(offset, segBuf);
            var segHeader = SegmentHeader.ReadFrom(segBuf);

            long contentStart = offset + SegmentHeader.Size;
            if (contentStart + segHeader.ContentLength > fileLength)
                break;

            var compression = FromFlags(segHeader.Flags);

            if (segHeader.Type == SegmentType.Data && compression == MeasCompression.None)
            {
                // Uncompressed data: parse chunk headers directly from MMF
                // without allocating the full segment content buffer
                ProcessDataDirect(contentStart, segHeader.ContentLength);
            }
            else
            {
                var content = new byte[segHeader.ContentLength];
                ReadBytes(contentStart, content);

                if (compression != MeasCompression.None)
                    content = Decompress(content, compression);

                switch (segHeader.Type)
                {
                    case SegmentType.Metadata:
                        ProcessMetadata(content);
                        break;
                    case SegmentType.Data:
                        ProcessData(content);
                        break;
                }
            }

            if (segHeader.NextSegmentOffset <= offset)
                break;
            offset = segHeader.NextSegmentOffset;
        }
    }

    private void ReadBytes(long position, Span<byte> buffer)
    {
        if (_readTemp == null || _readTemp.Length < buffer.Length)
            _readTemp = new byte[buffer.Length];
        _accessor!.ReadArray(position, _readTemp, 0, buffer.Length);
        _readTemp.AsSpan(0, buffer.Length).CopyTo(buffer);
    }

    // Reusable buffer for ReadArray calls (resized as needed)
    private byte[]? _readTemp;

    private void ReadBytes(long position, byte[] buffer)
    {
        _accessor!.ReadArray(position, buffer, 0, buffer.Length);
    }

    private unsafe void ProcessMetadata(byte[] content)
    {
        bool extended = (Header.Flags & FileHeader.FlagExtendedMetadata) != 0;
        var groupDefs = MetadataEncoder.Decode(content,
            extendedMetadata: extended,
            filePropertiesOut: extended ? _fileProperties : null);
        int globalIndex = 0;

        foreach (var gd in groupDefs)
        {
            var channels = new List<MeasChannel>();
            foreach (var cd in gd.Channels)
            {
                var channel = new MeasChannel(cd.Name, cd.DataType, globalIndex, cd.Properties);
                channel.SetMmfBasePointer(_mmfBasePtr);
                channels.Add(channel);
                while (_allChannels.Count <= globalIndex)
                    _allChannels.Add(null!);
                _allChannels[globalIndex] = channel;
                globalIndex++;
            }
            var group = new MeasGroup(gd.Name, gd.Properties, channels);

            if ((gd.Properties.TryGetValue("MEAS.bus_def", out var busDefValue)
                || gd.Properties.TryGetValue("meas.bus_def", out busDefValue))
                && busDefValue.Type == MeasDataType.Binary)
            {
                group.BusDefinition = BusMetadataEncoder.Decode(busDefValue.AsBinary());
            }

            _groups.Add(group);
            _groupsByName[gd.Name] = group;
        }
    }

    private void ProcessData(byte[] content)
    {
        int offset = 0;
        int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(content.AsSpan(offset));
        offset += 4;

        for (int i = 0; i < chunkCount; i++)
        {
            var (channelIndex, sampleCount, dataByteLength) =
                DataEncoder.ReadChunkHeader(content, ref offset);

            // Zero-copy: reference slice of segment content instead of allocating + copying
            int dataOffset = offset;
            offset += (int)dataByteLength;

            if (channelIndex < _allChannels.Count && _allChannels[channelIndex] != null)
            {
                _allChannels[channelIndex].AddChunk(
                    new DataChunkRef(sampleCount, content, dataOffset, (int)dataByteLength));
            }
        }
    }

    /// <summary>
    /// Parse an uncompressed data segment directly from the MMF without allocating
    /// a buffer for the full segment content. Only the chunk headers (~20 bytes each)
    /// are read; data chunk references point directly into the MMF.
    /// </summary>
    private void ProcessDataDirect(long contentStart, long contentLength)
    {
        // Read chunk count (4 bytes)
        Span<byte> countBuf = stackalloc byte[4];
        ReadBytes(contentStart, countBuf);
        int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(countBuf);

        long pos = contentStart + 4;
        Span<byte> hdrBuf = stackalloc byte[DataEncoder.ChunkHeaderSize];

        for (int i = 0; i < chunkCount; i++)
        {
            ReadBytes(pos, hdrBuf);
            int hdrOffset = 0;
            var (channelIndex, sampleCount, dataByteLength) =
                DataEncoder.ReadChunkHeader(hdrBuf, ref hdrOffset);
            pos += DataEncoder.ChunkHeaderSize;

            long dataFileOffset = pos;
            pos += dataByteLength;

            if (channelIndex < _allChannels.Count && _allChannels[channelIndex] != null)
            {
                _allChannels[channelIndex].AddChunk(
                    new DataChunkRef(sampleCount, _accessor!, dataFileOffset, (int)dataByteLength));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ptrAcquired)
        {
            _accessor!.SafeMemoryMappedViewHandle.ReleasePointer();
            _ptrAcquired = false;
        }
        _accessor?.Dispose();
        _mmf?.Dispose();
    }
}
