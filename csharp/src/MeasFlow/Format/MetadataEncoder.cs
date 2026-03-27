using System.Buffers.Binary;
using System.Text;

namespace MeasFlow.Format;

/// <summary>
/// Encodes/decodes the metadata segment containing group and channel definitions.
///
/// Wire format:
///   [int32: groupCount]
///   for each group:
///     [string: name] [int32: propertyCount] [properties...] [int32: channelCount]
///     for each channel:
///       [string: name] [byte: dataType] [int32: propertyCount] [properties...]
///
/// String format: [int32: byteLength] [utf8 bytes]
/// Property format: [string: key] [byte: valueType] [value bytes]
/// </summary>
internal static class MetadataEncoder
{
    // Current metadata format version (§6)
    public const byte MetaMajor = 0;
    public const byte MetaMinor = 1;

    /// <summary>
    /// Encode metadata. When <paramref name="extended"/> is true,
    /// the output starts with [metaMajor][metaMinor][fileProps...][groupCount]...
    /// </summary>
    public static byte[] Encode(IReadOnlyList<MeasGroupDefinition> groups,
        Dictionary<string, MeasValue>? fileProperties = null,
        bool extended = false)
    {
        // Auto-enable extended format when file properties are present
        extended = extended || fileProperties is { Count: > 0 };

        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        if (extended)
        {
            bw.Write(MetaMajor);  // uint8
            bw.Write(MetaMinor);  // uint8
            WriteProperties(bw, fileProperties ?? new Dictionary<string, MeasValue>());
        }

        bw.Write(groups.Count); // int32 group count

        foreach (var group in groups)
        {
            WriteString(bw, group.Name);
            WriteProperties(bw, group.Properties);

            bw.Write(group.Channels.Count); // int32 channel count
            foreach (var channel in group.Channels)
            {
                WriteString(bw, channel.Name);
                bw.Write((byte)channel.DataType);
                WriteProperties(bw, channel.Properties);
            }
        }

        bw.Flush();
        return ms.ToArray();
    }

    public static List<MeasGroupDefinition> Decode(ReadOnlySpan<byte> data,
        bool extendedMetadata = false,
        Dictionary<string, MeasValue>? filePropertiesOut = null)
    {
        int offset = 0;

        if (extendedMetadata)
        {
            byte major = data[offset++];
            byte minor = data[offset++];

            if (major > MetaMajor)
                throw new InvalidDataException(
                    $"Unsupported metadata version {major}.{minor} (max supported: {MetaMajor}.{MetaMinor})");

            if (major == MetaMajor && minor > MetaMinor)
                throw new InvalidDataException(
                    $"Unsupported metadata minor version {major}.{minor} (max supported: {MetaMajor}.{MetaMinor})");

            // File-level properties follow for all supported versions (currently 0.1).
            var fileProps = ReadProperties(data, ref offset);
            if (filePropertiesOut != null)
            {
                foreach (var kv in fileProps)
                    filePropertiesOut[kv.Key] = kv.Value;
            }
        }

        int groupCount = ReadInt32(data, ref offset);
        var groups = new List<MeasGroupDefinition>(groupCount);

        for (int g = 0; g < groupCount; g++)
        {
            string groupName = ReadString(data, ref offset);
            var groupProps = ReadProperties(data, ref offset);

            int channelCount = ReadInt32(data, ref offset);
            var channels = new List<MeasChannelDefinition>(channelCount);

            for (int c = 0; c < channelCount; c++)
            {
                string channelName = ReadString(data, ref offset);
                var dataType = (MeasDataType)data[offset++];
                var channelProps = ReadProperties(data, ref offset);
                channels.Add(new MeasChannelDefinition(channelName, dataType, channelProps));
            }

            groups.Add(new MeasGroupDefinition(groupName, groupProps, channels));
        }

        return groups;
    }

    private static void WriteString(BinaryWriter bw, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }

    private static void WriteProperties(BinaryWriter bw, Dictionary<string, MeasValue> props)
    {
        bw.Write(props.Count);
        foreach (var (key, value) in props)
        {
            WriteString(bw, key);
            bw.Write((byte)value.Type);
            switch (value.Type)
            {
                case MeasDataType.Int32:
                    bw.Write(value.AsInt32());
                    break;
                case MeasDataType.Int64:
                case MeasDataType.Timestamp:
                    bw.Write(value.AsInt64());
                    break;
                case MeasDataType.Float32:
                    bw.Write(value.AsFloat32());
                    break;
                case MeasDataType.Float64:
                    bw.Write(value.AsFloat64());
                    break;
                case MeasDataType.Utf8String:
                    WriteString(bw, value.AsString());
                    break;
                case MeasDataType.Bool:
                    bw.Write(value.AsBool());
                    break;
                case MeasDataType.Binary:
                    var bin = value.AsBinary();
                    bw.Write(bin.Length);
                    bw.Write(bin);
                    break;
                default:
                    bw.Write(value.AsInt64());
                    break;
            }
        }
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int offset)
    {
        int len = ReadInt32(data, ref offset);
        var str = Encoding.UTF8.GetString(data.Slice(offset, len));
        offset += len;
        return str;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, ref int offset)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        offset += 4;
        return value;
    }

    private static Dictionary<string, MeasValue> ReadProperties(ReadOnlySpan<byte> data, ref int offset)
    {
        int count = ReadInt32(data, ref offset);
        var props = new Dictionary<string, MeasValue>(count);

        for (int i = 0; i < count; i++)
        {
            string key = ReadString(data, ref offset);
            var type = (MeasDataType)data[offset++];
            MeasValue value = type switch
            {
                MeasDataType.Int32 => ReadInt32(data, ref offset) is var v ? (MeasValue)v : default,
                MeasDataType.Int64 => ReadInt64(data, ref offset),
                MeasDataType.Timestamp => new MeasTimestamp(ReadInt64(data, ref offset)),
                MeasDataType.Float32 => ReadFloat32(data, ref offset),
                MeasDataType.Float64 => ReadFloat64(data, ref offset),
                MeasDataType.Utf8String => ReadString(data, ref offset),
                MeasDataType.Bool => data[offset++] != 0,
                MeasDataType.Binary => ReadBinary(data, ref offset),
                _ => ReadInt64(data, ref offset),
            };
            props[key] = value;
        }

        return props;
    }

    private static long ReadInt64(ReadOnlySpan<byte> data, ref int offset)
    {
        long value = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
        offset += 8;
        return value;
    }

    private static float ReadFloat32(ReadOnlySpan<byte> data, ref int offset)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
        offset += 4;
        return value;
    }

    private static double ReadFloat64(ReadOnlySpan<byte> data, ref int offset)
    {
        double value = BinaryPrimitives.ReadDoubleLittleEndian(data[offset..]);
        offset += 8;
        return value;
    }

    private static byte[] ReadBinary(ReadOnlySpan<byte> data, ref int offset)
    {
        int len = ReadInt32(data, ref offset);
        var bytes = data.Slice(offset, len).ToArray();
        offset += len;
        return bytes;
    }
}

internal record MeasGroupDefinition(
    string Name,
    Dictionary<string, MeasValue> Properties,
    List<MeasChannelDefinition> Channels);

internal record MeasChannelDefinition(
    string Name,
    MeasDataType DataType,
    Dictionary<string, MeasValue> Properties);
