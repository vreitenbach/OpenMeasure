using System.Runtime.CompilerServices;

namespace OpenMeasure;

public enum OmxDataType : byte
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
    Timestamp = 0x20, // OmxTimestamp (int64 nanoseconds since Unix epoch)
    TimeSpan  = 0x21, // OmxTimeSpan (int64 nanoseconds)

    // Text & binary
    Utf8String = 0x30,
    Binary     = 0x31,

    // Boolean
    Bool = 0x50,
}

public static class OmxDataTypeExtensions
{
    /// <summary>Returns the fixed size in bytes, or -1 for variable-length types.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FixedSize(this OmxDataType type) => type switch
    {
        OmxDataType.Int8 => 1,
        OmxDataType.Int16 => 2,
        OmxDataType.Int32 => 4,
        OmxDataType.Int64 => 8,
        OmxDataType.UInt8 => 1,
        OmxDataType.UInt16 => 2,
        OmxDataType.UInt32 => 4,
        OmxDataType.UInt64 => 8,
        OmxDataType.Float32 => 4,
        OmxDataType.Float64 => 8,
        OmxDataType.Timestamp => 8,
        OmxDataType.TimeSpan => 8,
        OmxDataType.Bool => 1,
        _ => -1, // variable-length
    };

    public static bool IsFixedSize(this OmxDataType type) => type.FixedSize() > 0;

    public static OmxDataType FromClrType<T>() where T : unmanaged
    {
        if (typeof(T) == typeof(sbyte)) return OmxDataType.Int8;
        if (typeof(T) == typeof(short)) return OmxDataType.Int16;
        if (typeof(T) == typeof(int)) return OmxDataType.Int32;
        if (typeof(T) == typeof(long)) return OmxDataType.Int64;
        if (typeof(T) == typeof(byte)) return OmxDataType.UInt8;
        if (typeof(T) == typeof(ushort)) return OmxDataType.UInt16;
        if (typeof(T) == typeof(uint)) return OmxDataType.UInt32;
        if (typeof(T) == typeof(ulong)) return OmxDataType.UInt64;
        if (typeof(T) == typeof(float)) return OmxDataType.Float32;
        if (typeof(T) == typeof(double)) return OmxDataType.Float64;
        if (typeof(T) == typeof(OmxTimestamp)) return OmxDataType.Timestamp;
        if (typeof(T) == typeof(OmxTimeSpan)) return OmxDataType.TimeSpan;
        if (typeof(T) == typeof(bool)) return OmxDataType.Bool;

        throw new NotSupportedException($"Type {typeof(T).Name} is not a supported OMX data type.");
    }
}
