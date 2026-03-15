using System.Buffers.Binary;
using MeasFlow.Format;
using static MeasFlow.Format.SegmentCompressor;

namespace MeasFlow;

/// <summary>
/// Reader for MEAS files. Reads metadata lazily, provides typed access to channels.
/// </summary>
public sealed class MeasReader : IDisposable
{
    internal FileHeader Header { get; private set; }
    public IReadOnlyList<MeasGroup> Groups => _groups;
    public IReadOnlyDictionary<string, MeasValue> Properties => _fileProperties;
    public MeasTimestamp CreatedAt => new(Header.CreatedAtNanos);

    private readonly FileStream _stream;
    private readonly List<MeasGroup> _groups = [];
    private readonly Dictionary<string, MeasGroup> _groupsByName = [];
    private readonly Dictionary<string, MeasValue> _fileProperties = [];
    private readonly List<MeasChannel> _allChannels = []; // flat list indexed by globalIndex
    private bool _disposed;

    private MeasReader(FileStream stream)
    {
        _stream = stream;
    }

    public static MeasReader Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 64 * 1024);
        var reader = new MeasReader(stream);
        reader.ReadFile();
        return reader;
    }

    public MeasGroup this[string groupName] =>
        _groupsByName.TryGetValue(groupName, out var g)
            ? g
            : throw new KeyNotFoundException($"Group '{groupName}' not found.");

    public bool TryGetGroup(string name, out MeasGroup? group)
        => _groupsByName.TryGetValue(name, out group);

    private void ReadFile()
    {
        // Read file header
        Span<byte> headerBuf = stackalloc byte[FileHeader.Size];
        _stream.ReadExactly(headerBuf);
        Header = FileHeader.ReadFrom(headerBuf);

        // Walk segments using the forward-linked segment chain.
        // When SegmentCount > 0 (file closed normally), use it as the upper bound.
        // When SegmentCount == 0 (writer still open / streaming), walk until
        // we run out of readable data — this enables concurrent read while writing.
        _stream.Seek(Header.FirstSegmentOffset, SeekOrigin.Begin);

        long fileLength = _stream.Length;
        long maxSegments = Header.SegmentCount > 0 ? Header.SegmentCount : long.MaxValue;

        Span<byte> segBuf = stackalloc byte[SegmentHeader.Size];
        for (long i = 0; i < maxSegments; i++)
        {
            long segStart = _stream.Position;

            // Not enough bytes left for a segment header → stop
            if (segStart + SegmentHeader.Size > fileLength)
                break;

            _stream.ReadExactly(segBuf);
            var segHeader = SegmentHeader.ReadFrom(segBuf);

            // Not enough bytes left for segment content → stop
            if (segStart + SegmentHeader.Size + segHeader.ContentLength > fileLength)
                break;

            var content = new byte[segHeader.ContentLength];
            _stream.ReadExactly(content);

            // Decompress if segment is compressed
            var compression = FromFlags(segHeader.Flags);
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

            if (segHeader.NextSegmentOffset <= segStart)
                break; // prevent infinite loops
            _stream.Seek(segHeader.NextSegmentOffset, SeekOrigin.Begin);
        }
    }

    private void ProcessMetadata(byte[] content)
    {
        var groupDefs = MetadataEncoder.Decode(content);
        int globalIndex = 0;

        foreach (var gd in groupDefs)
        {
            var channels = new List<MeasChannel>();
            foreach (var cd in gd.Channels)
            {
                var channel = new MeasChannel(cd.Name, cd.DataType, globalIndex, cd.Properties);
                channels.Add(channel);
                // Ensure _allChannels list is large enough
                while (_allChannels.Count <= globalIndex)
                    _allChannels.Add(null!);
                _allChannels[globalIndex] = channel;
                globalIndex++;
            }
            var group = new MeasGroup(gd.Name, gd.Properties, channels);

            // Deserialize bus definition if present
            if (gd.Properties.TryGetValue("meas.bus_def", out var busDefValue)
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

            var data = new byte[dataByteLength];
            content.AsSpan(offset, (int)dataByteLength).CopyTo(data);
            offset += (int)dataByteLength;

            if (channelIndex < _allChannels.Count && _allChannels[channelIndex] != null)
            {
                _allChannels[channelIndex].AddChunk(new DataChunkRef(sampleCount, data));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }
}
