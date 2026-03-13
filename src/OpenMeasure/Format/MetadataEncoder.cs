using System.Buffers.Binary;
using System.Text;

namespace OpenMeasure.Format;

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
    public static byte[] Encode(IReadOnlyList<OmxGroupDefinition> groups)
    {
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

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

    public static List<OmxGroupDefinition> Decode(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        int groupCount = ReadInt32(data, ref offset);
        var groups = new List<OmxGroupDefinition>(groupCount);

        for (int g = 0; g < groupCount; g++)
        {
            string groupName = ReadString(data, ref offset);
            var groupProps = ReadProperties(data, ref offset);

            int channelCount = ReadInt32(data, ref offset);
            var channels = new List<OmxChannelDefinition>(channelCount);

            for (int c = 0; c < channelCount; c++)
            {
                string channelName = ReadString(data, ref offset);
                var dataType = (OmxDataType)data[offset++];
                var channelProps = ReadProperties(data, ref offset);
                channels.Add(new OmxChannelDefinition(channelName, dataType, channelProps));
            }

            groups.Add(new OmxGroupDefinition(groupName, groupProps, channels));
        }

        return groups;
    }

    private static void WriteString(BinaryWriter bw, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }

    private static void WriteProperties(BinaryWriter bw, Dictionary<string, OmxValue> props)
    {
        bw.Write(props.Count);
        foreach (var (key, value) in props)
        {
            WriteString(bw, key);
            bw.Write((byte)value.Type);
            switch (value.Type)
            {
                case OmxDataType.Int32:
                    bw.Write(value.AsInt32());
                    break;
                case OmxDataType.Int64:
                case OmxDataType.Timestamp:
                    bw.Write(value.AsInt64());
                    break;
                case OmxDataType.Float32:
                    bw.Write(value.AsFloat32());
                    break;
                case OmxDataType.Float64:
                    bw.Write(value.AsFloat64());
                    break;
                case OmxDataType.Utf8String:
                    WriteString(bw, value.AsString());
                    break;
                case OmxDataType.Bool:
                    bw.Write(value.AsBool());
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

    private static Dictionary<string, OmxValue> ReadProperties(ReadOnlySpan<byte> data, ref int offset)
    {
        int count = ReadInt32(data, ref offset);
        var props = new Dictionary<string, OmxValue>(count);

        for (int i = 0; i < count; i++)
        {
            string key = ReadString(data, ref offset);
            var type = (OmxDataType)data[offset++];
            OmxValue value = type switch
            {
                OmxDataType.Int32 => ReadInt32(data, ref offset) is var v ? (OmxValue)v : default,
                OmxDataType.Int64 => ReadInt64(data, ref offset),
                OmxDataType.Timestamp => new OmxTimestamp(ReadInt64(data, ref offset)),
                OmxDataType.Float32 => ReadFloat32(data, ref offset),
                OmxDataType.Float64 => ReadFloat64(data, ref offset),
                OmxDataType.Utf8String => ReadString(data, ref offset),
                OmxDataType.Bool => data[offset++] != 0,
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
}

internal record OmxGroupDefinition(
    string Name,
    Dictionary<string, OmxValue> Properties,
    List<OmxChannelDefinition> Channels);

internal record OmxChannelDefinition(
    string Name,
    OmxDataType DataType,
    Dictionary<string, OmxValue> Properties);
