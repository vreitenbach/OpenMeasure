using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MeasFlow;

/// <summary>
/// High-resolution timestamp for measurement data.
/// Stores nanoseconds since Unix epoch (1970-01-01T00:00:00Z).
/// Range: ~1678 to ~2262 AD. Resolution: 1 nanosecond.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct MeasTimestamp : IEquatable<MeasTimestamp>, IComparable<MeasTimestamp>
{
    /// <summary>Nanoseconds since Unix epoch (1970-01-01T00:00:00Z).</summary>
    public readonly long Nanoseconds;

    private const long TicksPerNanosecond = 1; // identity
    private const long NanosecondsPerMicrosecond = 1_000L;
    private const long NanosecondsPerMillisecond = 1_000_000L;
    private const long NanosecondsPerSecond = 1_000_000_000L;
    private const long NanosecondsPerMinute = 60L * NanosecondsPerSecond;
    private const long NanosecondsPerHour = 60L * NanosecondsPerMinute;

    // .NET ticks are 100ns intervals since 0001-01-01
    private const long DotNetTicksPerNanosecond = 100;
    private const long UnixEpochDotNetTicks = 621355968000000000L; // DateTime(1970,1,1).Ticks

    private static readonly long _stopwatchFrequency = Stopwatch.Frequency;
    private static readonly long _stopwatchBaseNanos;
    private static readonly long _stopwatchBaseTicks;

    static MeasTimestamp()
    {
        // Capture a synchronized pair: Stopwatch tick + DateTime for high-res Now
        _stopwatchBaseTicks = Stopwatch.GetTimestamp();
        _stopwatchBaseNanos = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * NanosecondsPerMillisecond;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MeasTimestamp(long nanoseconds)
    {
        Nanoseconds = nanoseconds;
    }

    /// <summary>Current time with highest available resolution (Stopwatch-based).</summary>
    public static MeasTimestamp Now
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            long elapsed = Stopwatch.GetTimestamp() - _stopwatchBaseTicks;
            long elapsedNanos = elapsed * NanosecondsPerSecond / _stopwatchFrequency;
            return new MeasTimestamp(_stopwatchBaseNanos + elapsedNanos);
        }
    }

    /// <summary>Unix epoch (1970-01-01T00:00:00Z).</summary>
    public static readonly MeasTimestamp Epoch = new(0);

    /// <summary>Minimum representable timestamp.</summary>
    public static readonly MeasTimestamp MinValue = new(long.MinValue);

    /// <summary>Maximum representable timestamp.</summary>
    public static readonly MeasTimestamp MaxValue = new(long.MaxValue);

    // --- Factory methods ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MeasTimestamp FromSeconds(double unixSeconds)
        => new((long)(unixSeconds * NanosecondsPerSecond));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MeasTimestamp FromMilliseconds(long unixMilliseconds)
        => new(unixMilliseconds * NanosecondsPerMillisecond);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MeasTimestamp FromMicroseconds(long unixMicroseconds)
        => new(unixMicroseconds * NanosecondsPerMicrosecond);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MeasTimestamp FromNanoseconds(long unixNanoseconds)
        => new(unixNanoseconds);

    public static MeasTimestamp FromDateTimeOffset(DateTimeOffset dto)
    {
        long dotNetTicks = dto.UtcTicks;
        long unixTicks = dotNetTicks - UnixEpochDotNetTicks;
        return new MeasTimestamp(unixTicks * DotNetTicksPerNanosecond);
    }

    public static MeasTimestamp FromDateTime(DateTime dt)
    {
        var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        long unixTicks = utc.Ticks - UnixEpochDotNetTicks;
        return new MeasTimestamp(unixTicks * DotNetTicksPerNanosecond);
    }

    public static MeasTimestamp Parse(string iso8601)
        => FromDateTimeOffset(DateTimeOffset.Parse(iso8601));

    // --- Conversion to other types ---

    public DateTimeOffset ToDateTimeOffset()
    {
        long dotNetTicks = UnixEpochDotNetTicks + Nanoseconds / DotNetTicksPerNanosecond;
        return new DateTimeOffset(dotNetTicks, TimeSpan.Zero);
    }

    public DateTime ToDateTimeUtc()
    {
        long dotNetTicks = UnixEpochDotNetTicks + Nanoseconds / DotNetTicksPerNanosecond;
        return new DateTime(dotNetTicks, DateTimeKind.Utc);
    }

    public double ToUnixSeconds() => (double)Nanoseconds / NanosecondsPerSecond;
    public long ToUnixMilliseconds() => Nanoseconds / NanosecondsPerMillisecond;
    public long ToUnixMicroseconds() => Nanoseconds / NanosecondsPerMicrosecond;

    // --- Arithmetic ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MeasTimestamp operator +(MeasTimestamp ts, TimeSpan span)
        => new(ts.Nanoseconds + span.Ticks * DotNetTicksPerNanosecond);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MeasTimestamp operator -(MeasTimestamp ts, TimeSpan span)
        => new(ts.Nanoseconds - span.Ticks * DotNetTicksPerNanosecond);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MeasTimeSpan operator -(MeasTimestamp a, MeasTimestamp b)
        => new(a.Nanoseconds - b.Nanoseconds);

    // --- Comparison ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(MeasTimestamp other) => Nanoseconds.CompareTo(other.Nanoseconds);

    public static bool operator <(MeasTimestamp left, MeasTimestamp right) => left.Nanoseconds < right.Nanoseconds;
    public static bool operator >(MeasTimestamp left, MeasTimestamp right) => left.Nanoseconds > right.Nanoseconds;
    public static bool operator <=(MeasTimestamp left, MeasTimestamp right) => left.Nanoseconds <= right.Nanoseconds;
    public static bool operator >=(MeasTimestamp left, MeasTimestamp right) => left.Nanoseconds >= right.Nanoseconds;
    public static bool operator ==(MeasTimestamp left, MeasTimestamp right) => left.Nanoseconds == right.Nanoseconds;
    public static bool operator !=(MeasTimestamp left, MeasTimestamp right) => left.Nanoseconds != right.Nanoseconds;

    // --- Equality ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(MeasTimestamp other) => Nanoseconds == other.Nanoseconds;
    public override bool Equals(object? obj) => obj is MeasTimestamp ts && Equals(ts);
    public override int GetHashCode() => Nanoseconds.GetHashCode();

    // --- Formatting ---

    public override string ToString()
    {
        var dto = ToDateTimeOffset();
        long subSecondNanos = Nanoseconds % NanosecondsPerSecond;
        if (subSecondNanos < 0) subSecondNanos += NanosecondsPerSecond;
        return $"{dto:yyyy-MM-ddTHH:mm:ss}.{subSecondNanos:D9}Z";
    }

    /// <summary>Size in bytes when serialized.</summary>
    internal const int SerializedSize = sizeof(long);
}

/// <summary>
/// High-resolution time span. Stores nanoseconds.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct MeasTimeSpan : IEquatable<MeasTimeSpan>, IComparable<MeasTimeSpan>
{
    public readonly long Nanoseconds;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MeasTimeSpan(long nanoseconds) => Nanoseconds = nanoseconds;

    public static MeasTimeSpan FromSeconds(double seconds)
        => new((long)(seconds * 1_000_000_000L));

    public static MeasTimeSpan FromMilliseconds(long ms)
        => new(ms * 1_000_000L);

    public double TotalSeconds => (double)Nanoseconds / 1_000_000_000L;
    public double TotalMilliseconds => (double)Nanoseconds / 1_000_000L;

    public TimeSpan ToTimeSpan() => TimeSpan.FromTicks(Nanoseconds / 100);

    public int CompareTo(MeasTimeSpan other) => Nanoseconds.CompareTo(other.Nanoseconds);
    public bool Equals(MeasTimeSpan other) => Nanoseconds == other.Nanoseconds;
    public override bool Equals(object? obj) => obj is MeasTimeSpan ts && Equals(ts);
    public override int GetHashCode() => Nanoseconds.GetHashCode();
    public override string ToString() => $"{TotalSeconds:F9}s";

    public static bool operator ==(MeasTimeSpan a, MeasTimeSpan b) => a.Nanoseconds == b.Nanoseconds;
    public static bool operator !=(MeasTimeSpan a, MeasTimeSpan b) => a.Nanoseconds != b.Nanoseconds;
    public static bool operator <(MeasTimeSpan a, MeasTimeSpan b) => a.Nanoseconds < b.Nanoseconds;
    public static bool operator >(MeasTimeSpan a, MeasTimeSpan b) => a.Nanoseconds > b.Nanoseconds;
}
