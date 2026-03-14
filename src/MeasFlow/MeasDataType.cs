using System.Runtime.CompilerServices;

namespace MeasFlow;

public enum MeasDataType : byte
{
    // Integers
    Int8    = 0x01,
    Int16   = 0x02,
    Int32   = 0x03,
    Int64   = 0x04,
    UInt8   = 0x05,
    UInt16  = 0x06,
    UInt32  = 0x07,
    UInt64  = 0x08,

    // Floating point
    Float32  = 0x10,
    Float64  = 0x11,

    // Timestamps
    Timestamp = 0x20, // MeasTimestamp (int64 nanoseconds since Unix epoch)
    TimeSpan  = 0x21, // MeasTimeSpan (int64 nanoseconds)

    // Text & binary
    Utf8String = 0x30,
    Binary     = 0x31,

    // Boolean
    Bool = 0x50,
}

public static class MeasDataTypeExtensions
{
    /// <summary>Returns the fixed size in bytes, or -1 for variable-length types.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FixedSize(this MeasDataType type) => type switch
    {
        MeasDataType.Int8 => 1,
        MeasDataType.Int16 => 2,
        MeasDataType.Int32 => 4,
        MeasDataType.Int64 => 8,
        MeasDataType.UInt8 => 1,
        MeasDataType.UInt16 => 2,
        MeasDataType.UInt32 => 4,
        MeasDataType.UInt64 => 8,
        MeasDataType.Float32 => 4,
        MeasDataType.Float64 => 8,
        MeasDataType.Timestamp => 8,
        MeasDataType.TimeSpan => 8,
        MeasDataType.Bool => 1,
        _ => -1, // variable-length
    };

    public static bool IsFixedSize(this MeasDataType type) => type.FixedSize() > 0;

    public static MeasDataType FromClrType<T>() where T : unmanaged
    {
        if (typeof(T) == typeof(sbyte)) return MeasDataType.Int8;
        if (typeof(T) == typeof(short)) return MeasDataType.Int16;
        if (typeof(T) == typeof(int)) return MeasDataType.Int32;
        if (typeof(T) == typeof(long)) return MeasDataType.Int64;
        if (typeof(T) == typeof(byte)) return MeasDataType.UInt8;
        if (typeof(T) == typeof(ushort)) return MeasDataType.UInt16;
        if (typeof(T) == typeof(uint)) return MeasDataType.UInt32;
        if (typeof(T) == typeof(ulong)) return MeasDataType.UInt64;
        if (typeof(T) == typeof(float)) return MeasDataType.Float32;
        if (typeof(T) == typeof(double)) return MeasDataType.Float64;
        if (typeof(T) == typeof(MeasTimestamp)) return MeasDataType.Timestamp;
        if (typeof(T) == typeof(MeasTimeSpan)) return MeasDataType.TimeSpan;
        if (typeof(T) == typeof(bool)) return MeasDataType.Bool;

        throw new NotSupportedException($"Type {typeof(T).Name} is not a supported MEAS data type.");
    }
}
