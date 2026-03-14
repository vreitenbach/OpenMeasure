using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace OpenMeasure.Bus;

/// <summary>
/// Decodes signal values from raw frame payloads.
/// Supports Intel (little-endian) and Motorola (big-endian) byte order,
/// matching CAN DBC bit numbering conventions.
/// </summary>
public static class SignalDecoder
{
    /// <summary>
    /// Decode a physical signal value from a raw frame payload.
    /// physical = raw * factor + offset
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double DecodeSignal(ReadOnlySpan<byte> payload, SignalDefinition signal)
    {
        long rawValue = ExtractBits(payload, signal.StartBit, signal.BitLength, signal.ByteOrder);

        if (signal.DataType == SignalDataType.Signed && signal.BitLength < 64)
        {
            long signBit = 1L << (signal.BitLength - 1);
            rawValue = (rawValue ^ signBit) - signBit;
        }

        if (signal.DataType == SignalDataType.IeeeFloat && signal.BitLength == 32)
        {
            float f = BitConverter.Int32BitsToSingle((int)rawValue);
            return f * signal.Factor + signal.Offset;
        }

        if (signal.DataType == SignalDataType.IeeeDouble && signal.BitLength == 64)
        {
            double d = BitConverter.Int64BitsToDouble(rawValue);
            return d * signal.Factor + signal.Offset;
        }

        return rawValue * signal.Factor + signal.Offset;
    }

    /// <summary>
    /// Extract raw bits from payload using CAN DBC bit numbering.
    /// </summary>
    public static long ExtractBits(ReadOnlySpan<byte> data, int startBit, int bitLength, ByteOrder byteOrder)
    {
        if (bitLength <= 0 || bitLength > 64)
            throw new ArgumentOutOfRangeException(nameof(bitLength), "Bit length must be 1-64.");

        if (byteOrder == ByteOrder.LittleEndian)
            return ExtractBitsIntel(data, startBit, bitLength);
        else
            return ExtractBitsMotorola(data, startBit, bitLength);
    }

    /// <summary>
    /// Intel (little-endian) byte order extraction.
    /// StartBit = LSB bit position. Bits numbered 0..7 in byte 0, 8..15 in byte 1, etc.
    /// </summary>
    private static long ExtractBitsIntel(ReadOnlySpan<byte> data, int startBit, int bitLength)
    {
        long result = 0;
        int bitsRead = 0;

        int bitPos = startBit;
        while (bitsRead < bitLength)
        {
            int byteIndex = bitPos / 8;
            int bitIndex = bitPos % 8;

            if (byteIndex >= data.Length)
                break;

            int bitsAvailable = 8 - bitIndex;
            int bitsToRead = Math.Min(bitsAvailable, bitLength - bitsRead);

            int mask = (1 << bitsToRead) - 1;
            long bits = (data[byteIndex] >> bitIndex) & mask;

            result |= bits << bitsRead;

            bitsRead += bitsToRead;
            bitPos += bitsToRead;
        }

        return result;
    }

    /// <summary>
    /// Motorola (big-endian) byte order extraction.
    /// StartBit = MSB bit position using CAN DBC Motorola numbering.
    /// Bit numbering: byte0=[7,6,5,4,3,2,1,0], byte1=[15,14,13,12,11,10,9,8], ...
    /// </summary>
    private static long ExtractBitsMotorola(ReadOnlySpan<byte> data, int startBit, int bitLength)
    {
        long result = 0;
        int bitsRead = 0;

        int bitPos = startBit;
        while (bitsRead < bitLength)
        {
            int byteIndex = bitPos / 8;
            int bitInByte = bitPos % 8;

            if (byteIndex >= data.Length)
                break;

            // In Motorola format, we read from MSB position downward
            long bit = (data[byteIndex] >> bitInByte) & 1;
            result |= bit << (bitLength - 1 - bitsRead);

            bitsRead++;

            // Navigate to next bit in Motorola order
            if (bitInByte == 0)
                bitPos += 15; // jump to bit 7 of next byte
            else
                bitPos--;
        }

        return result;
    }

    /// <summary>
    /// Check if a multiplexed signal is active for the given multiplexer value.
    /// </summary>
    public static bool IsSignalActive(SignalDefinition signal, long muxValue)
    {
        if (signal.MultiplexCondition == null)
            return true; // non-multiplexed signals are always active

        return IsConditionMet(signal.MultiplexCondition, muxValue);
    }

    private static bool IsConditionMet(MultiplexCondition condition, long muxValue)
    {
        if (muxValue < condition.LowValue || muxValue > condition.HighValue)
            return false;

        // For nested MUX, parent condition must also be met
        // (parent MUX value would need to be provided separately in a real scenario)
        return true;
    }
}
