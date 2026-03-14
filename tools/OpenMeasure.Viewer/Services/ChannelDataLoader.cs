namespace OpenMeasure.Viewer.Services;

/// <summary>
/// Dispatches ReadAll&lt;T&gt; calls based on OmxDataType and converts to double[] for plotting.
/// </summary>
public static class ChannelDataLoader
{
    public static double[]? LoadAsDouble(OmxChannel channel)
    {
        try
        {
            return channel.DataType switch
            {
                OmxDataType.Float32 => Array.ConvertAll(channel.ReadAll<float>(), v => (double)v),
                OmxDataType.Float64 => channel.ReadAll<double>(),
                OmxDataType.Int8 => Array.ConvertAll(channel.ReadAll<sbyte>(), v => (double)v),
                OmxDataType.Int16 => Array.ConvertAll(channel.ReadAll<short>(), v => (double)v),
                OmxDataType.Int32 => Array.ConvertAll(channel.ReadAll<int>(), v => (double)v),
                OmxDataType.Int64 => Array.ConvertAll(channel.ReadAll<long>(), v => (double)v),
                OmxDataType.UInt8 => Array.ConvertAll(channel.ReadAll<byte>(), v => (double)v),
                OmxDataType.UInt16 => Array.ConvertAll(channel.ReadAll<ushort>(), v => (double)v),
                OmxDataType.UInt32 => Array.ConvertAll(channel.ReadAll<uint>(), v => (double)v),
                OmxDataType.UInt64 => Array.ConvertAll(channel.ReadAll<ulong>(), v => (double)v),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    public static double[]? LoadTimestampsAsSeconds(OmxGroup group)
    {
        // Find a timestamp channel in the group
        var tsChannel = group.Channels.FirstOrDefault(c => c.DataType == OmxDataType.Timestamp);
        if (tsChannel == null || tsChannel.SampleCount == 0) return null;

        var timestamps = tsChannel.ReadAll<OmxTimestamp>();
        if (timestamps.Length == 0) return null;

        long baseNanos = timestamps[0].Nanoseconds;
        var result = new double[timestamps.Length];
        for (int i = 0; i < timestamps.Length; i++)
        {
            result[i] = (timestamps[i].Nanoseconds - baseNanos) / 1_000_000_000.0;
        }
        return result;
    }

    public static bool IsPlottable(OmxDataType type) => type switch
    {
        OmxDataType.Float32 or OmxDataType.Float64 or
        OmxDataType.Int8 or OmxDataType.Int16 or OmxDataType.Int32 or OmxDataType.Int64 or
        OmxDataType.UInt8 or OmxDataType.UInt16 or OmxDataType.UInt32 or OmxDataType.UInt64
            => true,
        _ => false,
    };
}
