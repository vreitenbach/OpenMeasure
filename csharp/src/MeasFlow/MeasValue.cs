namespace MeasFlow;

/// <summary>
/// Tagged union for property values. Supports all MEAS data types as single values, strings, and binary.
/// </summary>
public readonly struct MeasValue : IEquatable<MeasValue>
{
    private readonly long _intValue;
    private readonly double _floatValue;
    private readonly object? _refValue; // string or byte[]
    private readonly MeasDataType _type;

    public MeasDataType Type => _type;

    private MeasValue(long intValue, MeasDataType type)
    {
        _refValue = null;
        _floatValue = 0;
        _intValue = intValue;
        _type = type;
    }

    private MeasValue(double floatValue, MeasDataType type)
    {
        _refValue = null;
        _intValue = 0;
        _floatValue = floatValue;
        _type = type;
    }

    private MeasValue(string stringValue)
    {
        _intValue = 0;
        _floatValue = 0;
        _refValue = stringValue;
        _type = MeasDataType.Utf8String;
    }

    private MeasValue(byte[] binaryValue)
    {
        _intValue = 0;
        _floatValue = 0;
        _refValue = binaryValue;
        _type = MeasDataType.Binary;
    }

    // Implicit conversions
    public static implicit operator MeasValue(int value) => new(value, MeasDataType.Int32);
    public static implicit operator MeasValue(long value) => new(value, MeasDataType.Int64);
    public static implicit operator MeasValue(float value) => new(value, MeasDataType.Float32);
    public static implicit operator MeasValue(double value) => new(value, MeasDataType.Float64);
    public static implicit operator MeasValue(string value) => new(value);
    public static implicit operator MeasValue(bool value) => new(value ? 1L : 0L, MeasDataType.Bool);
    public static implicit operator MeasValue(MeasTimestamp value) => new(value.Nanoseconds, MeasDataType.Timestamp);
    public static implicit operator MeasValue(byte[] value) => new(value);

    // Getters
    public int AsInt32() => (int)_intValue;
    public long AsInt64() => _intValue;
    public float AsFloat32() => (float)_floatValue;
    public double AsFloat64() => _floatValue;
    public string AsString() => _refValue as string ?? string.Empty;
    public bool AsBool() => _intValue != 0;
    public MeasTimestamp AsTimestamp() => new(_intValue);
    public byte[] AsBinary() => _refValue as byte[] ?? [];

    public bool Equals(MeasValue other)
    {
        if (_type != other._type) return false;
        return _type switch
        {
            MeasDataType.Utf8String => AsString() == other.AsString(),
            MeasDataType.Binary => AsBinary().AsSpan().SequenceEqual(other.AsBinary()),
            _ => _intValue == other._intValue,
        };
    }

    public override bool Equals(object? obj) => obj is MeasValue v && Equals(v);
    public override int GetHashCode() => _type switch
    {
        MeasDataType.Utf8String => HashCode.Combine(_type, _refValue),
        MeasDataType.Binary => HashCode.Combine(_type, AsBinary().Length),
        _ => HashCode.Combine(_type, _intValue),
    };

    public override string ToString() => _type switch
    {
        MeasDataType.Utf8String => AsString(),
        MeasDataType.Float32 => ((float)_floatValue).ToString("G"),
        MeasDataType.Float64 => _floatValue.ToString("G"),
        MeasDataType.Bool => _intValue != 0 ? "true" : "false",
        MeasDataType.Timestamp => new MeasTimestamp(_intValue).ToString(),
        MeasDataType.Binary => $"byte[{AsBinary().Length}]",
        _ => _intValue.ToString(),
    };
}
