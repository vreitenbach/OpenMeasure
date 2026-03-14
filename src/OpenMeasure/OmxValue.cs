namespace OpenMeasure;

/// <summary>
/// Tagged union for property values. Supports all OMX data types as single values, strings, and binary.
/// </summary>
public readonly struct OmxValue : IEquatable<OmxValue>
{
    private readonly long _intValue;
    private readonly double _floatValue;
    private readonly object? _refValue; // string or byte[]
    private readonly OmxDataType _type;

    public OmxDataType Type => _type;

    private OmxValue(long intValue, OmxDataType type)
    {
        _refValue = null;
        _floatValue = 0;
        _intValue = intValue;
        _type = type;
    }

    private OmxValue(double floatValue, OmxDataType type)
    {
        _refValue = null;
        _intValue = 0;
        _floatValue = floatValue;
        _type = type;
    }

    private OmxValue(string stringValue)
    {
        _intValue = 0;
        _floatValue = 0;
        _refValue = stringValue;
        _type = OmxDataType.Utf8String;
    }

    private OmxValue(byte[] binaryValue)
    {
        _intValue = 0;
        _floatValue = 0;
        _refValue = binaryValue;
        _type = OmxDataType.Binary;
    }

    // Implicit conversions
    public static implicit operator OmxValue(int value) => new(value, OmxDataType.Int32);
    public static implicit operator OmxValue(long value) => new(value, OmxDataType.Int64);
    public static implicit operator OmxValue(float value) => new(value, OmxDataType.Float32);
    public static implicit operator OmxValue(double value) => new(value, OmxDataType.Float64);
    public static implicit operator OmxValue(string value) => new(value);
    public static implicit operator OmxValue(bool value) => new(value ? 1L : 0L, OmxDataType.Bool);
    public static implicit operator OmxValue(OmxTimestamp value) => new(value.Nanoseconds, OmxDataType.Timestamp);
    public static implicit operator OmxValue(byte[] value) => new(value);

    // Getters
    public int AsInt32() => (int)_intValue;
    public long AsInt64() => _intValue;
    public float AsFloat32() => (float)_floatValue;
    public double AsFloat64() => _floatValue;
    public string AsString() => _refValue as string ?? string.Empty;
    public bool AsBool() => _intValue != 0;
    public OmxTimestamp AsTimestamp() => new(_intValue);
    public byte[] AsBinary() => _refValue as byte[] ?? [];

    public bool Equals(OmxValue other)
    {
        if (_type != other._type) return false;
        return _type switch
        {
            OmxDataType.Utf8String => AsString() == other.AsString(),
            OmxDataType.Binary => AsBinary().AsSpan().SequenceEqual(other.AsBinary()),
            _ => _intValue == other._intValue,
        };
    }

    public override bool Equals(object? obj) => obj is OmxValue v && Equals(v);
    public override int GetHashCode() => _type switch
    {
        OmxDataType.Utf8String => HashCode.Combine(_type, _refValue),
        OmxDataType.Binary => HashCode.Combine(_type, AsBinary().Length),
        _ => HashCode.Combine(_type, _intValue),
    };

    public override string ToString() => _type switch
    {
        OmxDataType.Utf8String => AsString(),
        OmxDataType.Float32 => ((float)_floatValue).ToString("G"),
        OmxDataType.Float64 => _floatValue.ToString("G"),
        OmxDataType.Bool => _intValue != 0 ? "true" : "false",
        OmxDataType.Timestamp => new OmxTimestamp(_intValue).ToString(),
        OmxDataType.Binary => $"byte[{AsBinary().Length}]",
        _ => _intValue.ToString(),
    };
}
