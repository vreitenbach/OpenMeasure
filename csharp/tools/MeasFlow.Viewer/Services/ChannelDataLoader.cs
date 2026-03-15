namespace MeasFlow.Viewer.Services;

/// <summary>
/// Dispatches ReadAll&lt;T&gt; calls based on MeasDataType and converts to double[] for plotting.
/// </summary>
public static class ChannelDataLoader
{
    public static double[]? LoadAsDouble(MeasChannel channel)
    {
        try
        {
            return channel.DataType switch
            {
                MeasDataType.Float32 => Array.ConvertAll(channel.ReadAll<float>(), v => (double)v),
                MeasDataType.Float64 => channel.ReadAll<double>(),
                MeasDataType.Int8 => Array.ConvertAll(channel.ReadAll<sbyte>(), v => (double)v),
                MeasDataType.Int16 => Array.ConvertAll(channel.ReadAll<short>(), v => (double)v),
                MeasDataType.Int32 => Array.ConvertAll(channel.ReadAll<int>(), v => (double)v),
                MeasDataType.Int64 => Array.ConvertAll(channel.ReadAll<long>(), v => (double)v),
                MeasDataType.UInt8 => Array.ConvertAll(channel.ReadAll<byte>(), v => (double)v),
                MeasDataType.UInt16 => Array.ConvertAll(channel.ReadAll<ushort>(), v => (double)v),
                MeasDataType.UInt32 => Array.ConvertAll(channel.ReadAll<uint>(), v => (double)v),
                MeasDataType.UInt64 => Array.ConvertAll(channel.ReadAll<ulong>(), v => (double)v),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    public static double[]? LoadTimestampsAsSeconds(MeasGroup group)
    {
        // Find a timestamp channel in the group
        var tsChannel = group.Channels.FirstOrDefault(c => c.DataType == MeasDataType.Timestamp);
        if (tsChannel == null || tsChannel.SampleCount == 0) return null;

        var timestamps = tsChannel.ReadAll<MeasTimestamp>();
        if (timestamps.Length == 0) return null;

        long baseNanos = timestamps[0].Nanoseconds;
        var result = new double[timestamps.Length];
        for (int i = 0; i < timestamps.Length; i++)
        {
            result[i] = (timestamps[i].Nanoseconds - baseNanos) / 1_000_000_000.0;
        }
        return result;
    }

    public static bool IsPlottable(MeasDataType type) => type switch
    {
        MeasDataType.Float32 or MeasDataType.Float64 or
        MeasDataType.Int8 or MeasDataType.Int16 or MeasDataType.Int32 or MeasDataType.Int64 or
        MeasDataType.UInt8 or MeasDataType.UInt16 or MeasDataType.UInt32 or MeasDataType.UInt64
            => true,
        _ => false,
    };
}
