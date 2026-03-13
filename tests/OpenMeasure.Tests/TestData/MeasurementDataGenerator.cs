namespace OpenMeasure.Tests.TestData;

/// <summary>
/// Generates realistic measurement data for tests, simulating a
/// vehicle test bench with motor sensors and CAN bus data.
/// </summary>
public static class MeasurementDataGenerator
{
    /// <summary>
    /// Generates a timestamp array with equidistant intervals.
    /// </summary>
    public static OmxTimestamp[] GenerateTimestamps(
        DateTimeOffset start, int sampleCount, double sampleRateHz)
    {
        var timestamps = new OmxTimestamp[sampleCount];
        long intervalNanos = (long)(1_000_000_000.0 / sampleRateHz);
        long startNanos = OmxTimestamp.FromDateTimeOffset(start).Nanoseconds;

        for (int i = 0; i < sampleCount; i++)
            timestamps[i] = new OmxTimestamp(startNanos + i * intervalNanos);

        return timestamps;
    }

    /// <summary>
    /// Simulates RPM signal: idle → ramp up → hold → ramp down.
    /// </summary>
    public static float[] GenerateRpmProfile(int sampleCount, float idleRpm = 800f, float maxRpm = 6500f)
    {
        var rpm = new float[sampleCount];
        var rng = new Random(42);

        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleCount;
            float baseRpm = t switch
            {
                < 0.1f => idleRpm,
                < 0.4f => idleRpm + (maxRpm - idleRpm) * ((t - 0.1f) / 0.3f),
                < 0.7f => maxRpm,
                < 0.9f => maxRpm - (maxRpm - idleRpm) * ((t - 0.7f) / 0.2f),
                _ => idleRpm,
            };
            rpm[i] = baseRpm + (float)(rng.NextDouble() * 20 - 10); // ±10 RPM noise
        }

        return rpm;
    }

    /// <summary>
    /// Simulates temperature rising with RPM load.
    /// </summary>
    public static double[] GenerateTemperature(float[] rpm, double ambientTemp = 22.0)
    {
        var temp = new double[rpm.Length];
        double currentTemp = ambientTemp;
        var rng = new Random(123);

        for (int i = 0; i < rpm.Length; i++)
        {
            double targetTemp = ambientTemp + (rpm[i] - 800) / 6500.0 * 70.0;
            currentTemp += (targetTemp - currentTemp) * 0.001; // thermal inertia
            temp[i] = currentTemp + rng.NextDouble() * 0.5 - 0.25; // ±0.25°C noise
        }

        return temp;
    }

    /// <summary>
    /// Simulates vibration data (high-frequency acceleration).
    /// </summary>
    public static float[] GenerateVibration(int sampleCount, float amplitude = 2.0f)
    {
        var data = new float[sampleCount];
        var rng = new Random(77);

        for (int i = 0; i < sampleCount; i++)
        {
            float t = i * 0.001f; // 1kHz assumed
            data[i] = amplitude * MathF.Sin(2 * MathF.PI * 120 * t)   // 120 Hz base
                     + amplitude * 0.3f * MathF.Sin(2 * MathF.PI * 360 * t) // 3rd harmonic
                     + (float)(rng.NextDouble() * 0.5 - 0.25); // noise
        }

        return data;
    }

    /// <summary>
    /// Generates simulated CAN frames (8 bytes each, typical CAN 2.0).
    /// Each frame = [2 bytes CAN-ID] [1 byte DLC] [up to 8 bytes data].
    /// </summary>
    public static (byte[][] frames, OmxTimestamp[] timestamps) GenerateCanFrames(
        DateTimeOffset start, int frameCount, double frameRateHz = 1000)
    {
        var frames = new byte[frameCount][];
        var timestamps = new OmxTimestamp[frameCount];
        var rng = new Random(99);
        long intervalNanos = (long)(1_000_000_000.0 / frameRateHz);
        long startNanos = OmxTimestamp.FromDateTimeOffset(start).Nanoseconds;

        // Typical CAN message IDs
        ushort[] canIds = [0x100, 0x200, 0x301, 0x400, 0x7DF];

        for (int i = 0; i < frameCount; i++)
        {
            timestamps[i] = new OmxTimestamp(startNanos + i * intervalNanos);

            ushort canId = canIds[i % canIds.Length];
            byte dlc = 8;
            var frame = new byte[2 + 1 + dlc]; // ID + DLC + Data
            frame[0] = (byte)(canId & 0xFF);
            frame[1] = (byte)(canId >> 8);
            frame[2] = dlc;
            rng.NextBytes(frame.AsSpan(3));

            // Embed a realistic engine RPM signal in CAN ID 0x100
            if (canId == 0x100)
            {
                float rpm = 800 + (float)i / frameCount * 5700;
                ushort rpmRaw = (ushort)(rpm * 4); // typical scaling: 0.25 RPM/bit
                frame[3] = (byte)(rpmRaw & 0xFF);
                frame[4] = (byte)(rpmRaw >> 8);
            }

            frames[i] = frame;
        }

        return (frames, timestamps);
    }

    /// <summary>
    /// Decodes RPM signal from generated CAN frames (CAN ID 0x100, bytes 3-4, factor 0.25).
    /// </summary>
    public static float[] DecodeRpmFromCanFrames(byte[][] frames)
    {
        var rpmValues = new List<float>();
        foreach (var frame in frames)
        {
            ushort canId = (ushort)(frame[0] | (frame[1] << 8));
            if (canId == 0x100)
            {
                ushort raw = (ushort)(frame[3] | (frame[4] << 8));
                rpmValues.Add(raw * 0.25f);
            }
        }
        return rpmValues.ToArray();
    }
}
