using System.Runtime.CompilerServices;

namespace MeasFlow;

/// <summary>
/// Incrementally computed channel statistics using Welford's online algorithm.
/// Updated O(1) per sample during writes. Stored in channel metadata for instant access on read.
/// </summary>
public readonly struct ChannelStatistics
{
    public long Count { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Sum { get; init; }
    public double Mean { get; init; }

    /// <summary>Population variance (Welford's M2 / count).</summary>
    public double Variance { get; init; }

    /// <summary>Population standard deviation.</summary>
    public double StdDev => Count > 0 ? Math.Sqrt(Variance) : 0;

    /// <summary>First value written.</summary>
    public double First { get; init; }

    /// <summary>Last value written.</summary>
    public double Last { get; init; }

    public bool HasData => Count > 0;

    public override string ToString() =>
        HasData
            ? $"n={Count} min={Min:G6} max={Max:G6} mean={Mean:G6} stddev={StdDev:G6}"
            : "empty";

    // --- Property keys for serialization ---
    internal const string PropPrefix = "meas.stats.";
    internal const string PropCount = "meas.stats.count";
    internal const string PropMin = "meas.stats.min";
    internal const string PropMax = "meas.stats.max";
    internal const string PropSum = "meas.stats.sum";
    internal const string PropMean = "meas.stats.mean";
    internal const string PropVariance = "meas.stats.variance";
    internal const string PropFirst = "meas.stats.first";
    internal const string PropLast = "meas.stats.last";

    internal void WriteToProperties(Dictionary<string, MeasValue> props)
    {
        if (Count == 0) return;
        props[PropCount] = Count;
        props[PropMin] = Min;
        props[PropMax] = Max;
        props[PropSum] = Sum;
        props[PropMean] = Mean;
        props[PropVariance] = Variance;
        props[PropFirst] = First;
        props[PropLast] = Last;
    }

    internal static ChannelStatistics? ReadFromProperties(IReadOnlyDictionary<string, MeasValue> props)
    {
        if (!props.TryGetValue(PropCount, out var countVal))
            return null;

        return new ChannelStatistics
        {
            Count = countVal.AsInt64(),
            Min = props.TryGetValue(PropMin, out var v) ? v.AsFloat64() : 0,
            Max = props.TryGetValue(PropMax, out v) ? v.AsFloat64() : 0,
            Sum = props.TryGetValue(PropSum, out v) ? v.AsFloat64() : 0,
            Mean = props.TryGetValue(PropMean, out v) ? v.AsFloat64() : 0,
            Variance = props.TryGetValue(PropVariance, out v) ? v.AsFloat64() : 0,
            First = props.TryGetValue(PropFirst, out v) ? v.AsFloat64() : 0,
            Last = props.TryGetValue(PropLast, out v) ? v.AsFloat64() : 0,
        };
    }
}

/// <summary>
/// Mutable accumulator for Welford's online algorithm. Used during writes.
/// </summary>
internal struct StatisticsAccumulator
{
    private long _count;
    private double _min;
    private double _max;
    private double _sum;
    private double _mean;
    private double _m2; // Welford's M2 for variance
    private double _first;
    private double _last;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(double value)
    {
        _count++;
        _last = value;

        if (_count == 1)
        {
            _first = value;
            _min = value;
            _max = value;
            _sum = value;
            _mean = value;
            _m2 = 0;
            return;
        }

        if (value < _min) _min = value;
        if (value > _max) _max = value;
        _sum += value;

        // Welford's online algorithm
        double delta = value - _mean;
        _mean += delta / _count;
        double delta2 = value - _mean;
        _m2 += delta * delta2;
    }

    public readonly ChannelStatistics ToStatistics() => new()
    {
        Count = _count,
        Min = _min,
        Max = _max,
        Sum = _sum,
        Mean = _mean,
        Variance = _count > 1 ? _m2 / _count : 0,
        First = _first,
        Last = _last,
    };
}

/// <summary>
/// Converts unmanaged numeric types to double for statistics computation.
/// </summary>
internal static class NumericConverter
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToDouble<T>(T value) where T : unmanaged
    {
        if (typeof(T) == typeof(float)) return Unsafe.As<T, float>(ref value);
        if (typeof(T) == typeof(double)) return Unsafe.As<T, double>(ref value);
        if (typeof(T) == typeof(int)) return Unsafe.As<T, int>(ref value);
        if (typeof(T) == typeof(long)) return Unsafe.As<T, long>(ref value);
        if (typeof(T) == typeof(short)) return Unsafe.As<T, short>(ref value);
        if (typeof(T) == typeof(byte)) return Unsafe.As<T, byte>(ref value);
        if (typeof(T) == typeof(sbyte)) return Unsafe.As<T, sbyte>(ref value);
        if (typeof(T) == typeof(ushort)) return Unsafe.As<T, ushort>(ref value);
        if (typeof(T) == typeof(uint)) return Unsafe.As<T, uint>(ref value);
        if (typeof(T) == typeof(ulong)) return Unsafe.As<T, ulong>(ref value);
        if (typeof(T) == typeof(MeasTimestamp)) return Unsafe.As<T, MeasTimestamp>(ref value).Nanoseconds;
        return 0; // bool, MeasTimeSpan, etc. — no meaningful stats
    }

    public static bool IsNumeric<T>() where T : unmanaged =>
        typeof(T) == typeof(float) || typeof(T) == typeof(double) ||
        typeof(T) == typeof(int) || typeof(T) == typeof(long) ||
        typeof(T) == typeof(short) || typeof(T) == typeof(byte) ||
        typeof(T) == typeof(sbyte) || typeof(T) == typeof(ushort) ||
        typeof(T) == typeof(uint) || typeof(T) == typeof(ulong);
}
