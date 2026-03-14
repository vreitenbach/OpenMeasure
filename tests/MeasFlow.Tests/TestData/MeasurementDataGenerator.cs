using MeasFlow.Bus;

namespace MeasFlow.Tests.TestData;

/// <summary>
/// Generates realistic measurement data for tests, simulating a
/// vehicle test bench with motor sensors and CAN bus data.
/// </summary>
public static class MeasurementDataGenerator
{
    /// <summary>
    /// Generates a timestamp array with equidistant intervals.
    /// </summary>
    public static MeasTimestamp[] GenerateTimestamps(
        DateTimeOffset start, int sampleCount, double sampleRateHz)
    {
        var timestamps = new MeasTimestamp[sampleCount];
        long intervalNanos = (long)(1_000_000_000.0 / sampleRateHz);
        long startNanos = MeasTimestamp.FromDateTimeOffset(start).Nanoseconds;

        for (int i = 0; i < sampleCount; i++)
            timestamps[i] = new MeasTimestamp(startNanos + i * intervalNanos);

        return timestamps;
    }

    /// <summary>
    /// Simulates RPM signal: idle -> ramp up -> hold -> ramp down.
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
            rpm[i] = baseRpm + (float)(rng.NextDouble() * 20 - 10);
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
            currentTemp += (targetTemp - currentTemp) * 0.001;
            temp[i] = currentTemp + rng.NextDouble() * 0.5 - 0.25;
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
            float t = i * 0.001f;
            data[i] = amplitude * MathF.Sin(2 * MathF.PI * 120 * t)
                     + amplitude * 0.3f * MathF.Sin(2 * MathF.PI * 360 * t)
                     + (float)(rng.NextDouble() * 0.5 - 0.25);
        }

        return data;
    }

    /// <summary>
    /// Writes CAN bus test data using the BusGroupWriter API.
    /// Generates frames for multiple CAN IDs with an embedded RPM signal.
    /// </summary>
    public static void WriteCanBusData(MeasWriter writer, string groupName,
        DateTimeOffset start, int frameCount, double frameRateHz = 1000)
    {
        var can = writer.AddBusGroup(groupName, new CanBusConfig { BaudRate = 500_000 });

        // Define frames with signal definitions on the FRAME, not on channels
        var engineFrame = can.DefineCanFrame("EngineData", 0x100, 8);
        engineFrame.Signals.Add(new SignalDefinition
        {
            Name = "EngineRPM",
            StartBit = 0,
            BitLength = 16,
            Factor = 0.25,
            Unit = "rpm",
        });
        engineFrame.Signals.Add(new SignalDefinition
        {
            Name = "EngineTemp",
            StartBit = 16,
            BitLength = 8,
            Factor = 1.0,
            Offset = -40,
            Unit = "degC",
        });

        can.DefineCanFrame("BrakeData", 0x200, 8);
        can.DefineCanFrame("SteeringAngle", 0x301, 8);
        can.DefineCanFrame("TransmissionData", 0x400, 8);
        can.DefineCanFrame("DiagRequest", 0x7DF, 8);

        uint[] canIds = [0x100, 0x200, 0x301, 0x400, 0x7DF];
        var rng = new Random(99);
        long intervalNanos = (long)(1_000_000_000.0 / frameRateHz);
        long startNanos = MeasTimestamp.FromDateTimeOffset(start).Nanoseconds;

        for (int i = 0; i < frameCount; i++)
        {
            var ts = new MeasTimestamp(startNanos + i * intervalNanos);
            uint canId = canIds[i % canIds.Length];

            var payload = new byte[8];
            rng.NextBytes(payload);

            if (canId == 0x100)
            {
                float rpm = 800 + (float)i / frameCount * 5700;
                ushort rpmRaw = (ushort)(rpm / 0.25);
                payload[0] = (byte)(rpmRaw & 0xFF);
                payload[1] = (byte)(rpmRaw >> 8);
                payload[2] = 130; // 90 degC
            }

            can.WriteFrame(ts, canId, payload);
        }
    }
}
