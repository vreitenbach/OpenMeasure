using System.Buffers.Binary;
using OpenMeasure.Format;

namespace OpenMeasure;

/// <summary>
/// Reader for OMX files. Reads metadata lazily, provides typed access to channels.
/// </summary>
public sealed class OmxReader : IDisposable
{
    internal FileHeader Header { get; private set; }
    public IReadOnlyList<OmxGroup> Groups => _groups;
    public IReadOnlyDictionary<string, OmxValue> Properties => _fileProperties;
    public OmxTimestamp CreatedAt => new(Header.CreatedAtNanos);

    private readonly FileStream _stream;
    private readonly List<OmxGroup> _groups = [];
    private readonly Dictionary<string, OmxGroup> _groupsByName = [];
    private readonly Dictionary<string, OmxValue> _fileProperties = [];
    private readonly List<OmxChannel> _allChannels = []; // flat list indexed by globalIndex
    private bool _disposed;

    private OmxReader(FileStream stream)
    {
        _stream = stream;
    }

    public static OmxReader Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024);
        var reader = new OmxReader(stream);
        reader.ReadFile();
        return reader;
    }

    public OmxGroup this[string groupName] =>
        _groupsByName.TryGetValue(groupName, out var g)
            ? g
            : throw new KeyNotFoundException($"Group '{groupName}' not found.");

    public bool TryGetGroup(string name, out OmxGroup? group)
        => _groupsByName.TryGetValue(name, out group);

    private void ReadFile()
    {
        // Read file header
        Span<byte> headerBuf = stackalloc byte[FileHeader.Size];
        _stream.ReadExactly(headerBuf);
        Header = FileHeader.ReadFrom(headerBuf);

        // Walk segments
        _stream.Seek(Header.FirstSegmentOffset, SeekOrigin.Begin);

        Span<byte> segBuf = stackalloc byte[SegmentHeader.Size];
        for (long i = 0; i < Header.SegmentCount; i++)
        {
            long segStart = _stream.Position;
            _stream.ReadExactly(segBuf);
            var segHeader = SegmentHeader.ReadFrom(segBuf);

            var content = new byte[segHeader.ContentLength];
            _stream.ReadExactly(content);

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
            var channels = new List<OmxChannel>();
            foreach (var cd in gd.Channels)
            {
                var channel = new OmxChannel(cd.Name, cd.DataType, globalIndex, cd.Properties);
                channels.Add(channel);
                // Ensure _allChannels list is large enough
                while (_allChannels.Count <= globalIndex)
                    _allChannels.Add(null!);
                _allChannels[globalIndex] = channel;
                globalIndex++;
            }
            var group = new OmxGroup(gd.Name, gd.Properties, channels);
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
