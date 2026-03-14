using OpenMeasure;
using OpenMeasure.Bus;

// === WRITE: Motor test bench measurement ===
Console.WriteLine("=== Writing measurement data ===");

using (var writer = OmxFile.CreateWriter("demo_measurement.omx"))
{
    // Group 1: Analog sensors with timestamps
    var motor = writer.AddGroup("Motor");
    motor.Properties["Prüfstand"] = "P42";
    motor.Properties["Operator"] = "Max Mustermann";
    motor.Properties["TestStart"] = OmxTimestamp.Now;

    var time = motor.AddChannel<OmxTimestamp>("Time");
    var rpm = motor.AddChannel<float>("RPM");
    var temp = motor.AddChannel<double>("OilTemperature");

    rpm.Properties["Unit"] = "1/min";
    temp.Properties["Unit"] = "°C";

    // Simulate 1 second of data at 1kHz
    var startTime = OmxTimestamp.Now;
    var rng = new Random(42);
    for (int i = 0; i < 1000; i++)
    {
        time.Write(startTime + TimeSpan.FromMilliseconds(i));
        rpm.Write(3000.0f + (float)(Math.Sin(i * 0.01) * 500) + (float)(rng.NextDouble() * 10));
        temp.Write(92.0 + Math.Sin(i * 0.005) * 3.0);
    }

    // Group 2: CAN bus with structured frame/signal definitions
    var can = writer.AddBusGroup("CAN_Powertrain", new CanBusConfig { BaudRate = 500_000 });

    // Frame definitions with signals — IDs belong to frames, not signals
    var engineFrame = can.DefineCanFrame("EngineData", frameId: 0x100, payloadLength: 8);
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

    can.DefineCanFrame("TransmissionData", frameId: 0x200, payloadLength: 8)
        .Signals.Add(new SignalDefinition
        {
            Name = "GearPosition",
            StartBit = 0,
            BitLength = 4,
            ValueDescriptions = new()
            {
                [0] = "Park", [1] = "Reverse", [2] = "Neutral",
                [3] = "Drive", [4] = "Sport",
            },
        });

    // Write CAN frames with standardized wire format
    for (int i = 0; i < 100; i++)
    {
        var ts = startTime + TimeSpan.FromMilliseconds(i * 10);
        ushort rpmRaw = (ushort)((3000 + i * 10) / 0.25);
        can.WriteFrame(ts, 0x100, new byte[]
        {
            (byte)(rpmRaw & 0xFF), (byte)(rpmRaw >> 8),
            130, // Temp: 130-40=90°C
            0x00, 0x00, 0x00, 0x00, 0x00,
        });

        if (i % 5 == 0)
        {
            can.WriteFrame(ts, 0x200, new byte[]
            {
                0x03, // Gear = Drive
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            });
        }
    }

    Console.WriteLine("  Written: Motor group (1000 samples @ 1kHz)");
    Console.WriteLine("  Written: CAN group (100+ frames with structured signal definitions)");
}

// === READ: Open and inspect the file ===
Console.WriteLine("\n=== Reading measurement data ===");

using (var reader = OmxFile.OpenRead("demo_measurement.omx"))
{
    Console.WriteLine($"  File created: {reader.CreatedAt}");
    Console.WriteLine($"  Groups: {reader.Groups.Count}");

    foreach (var group in reader.Groups)
    {
        Console.WriteLine($"\n  Group: {group.Name}");

        if (group.BusDefinition != null)
        {
            var busCfg = group.BusDefinition.BusConfig;
            Console.WriteLine($"    Bus Type: {busCfg.BusType}");
            if (busCfg is CanBusConfig canCfg)
                Console.WriteLine($"    Baud Rate: {canCfg.BaudRate}");

            foreach (var frame in group.BusDefinition.Frames)
            {
                Console.WriteLine($"    Frame: {frame.Name} (ID=0x{frame.FrameId:X})");
                foreach (var sig in frame.Signals)
                    Console.WriteLine($"      Signal: {sig.Name} [{sig.StartBit}:{sig.BitLength}] " +
                        $"factor={sig.Factor} offset={sig.Offset} unit={sig.Unit}");
            }

            // Decode signals directly from raw frames
            Console.WriteLine("\n    Decoded signals from raw frames:");
            var rpmValues = group.DecodeSignal("EngineRPM");
            Console.WriteLine($"      EngineRPM: {rpmValues.Length} values, " +
                $"first={rpmValues[0]:F1}, last={rpmValues[^1]:F1}");

            var gearValues = group.DecodeSignal("GearPosition");
            Console.WriteLine($"      GearPosition: {gearValues.Length} values, " +
                $"mode={gearValues[0]:F0} " +
                $"({group.BusDefinition.FindSignal("GearPosition")?.Signal.ValueDescriptions?[(long)gearValues[0]]})");
        }
        else
        {
            foreach (var (key, value) in group.Properties)
                Console.WriteLine($"    {key} = {value}");

            foreach (var channel in group.Channels)
                Console.WriteLine($"    Channel: {channel.Name} [{channel.DataType}] ({channel.SampleCount} samples)");
        }
    }

    // Read analog data
    var rpmData = reader["Motor"]["RPM"].ReadAll<float>();
    var timeData = reader["Motor"]["Time"].ReadAll<OmxTimestamp>();
    Console.WriteLine($"\n  Motor RPM: first={rpmData[0]:F1}, last={rpmData[^1]:F1}");
    Console.WriteLine($"  Time range: {timeData[0]} → {timeData[^1]}");
    Console.WriteLine($"  Duration: {(timeData[^1] - timeData[0]).TotalMilliseconds:F1} ms");

    var fileSize = new FileInfo("demo_measurement.omx").Length;
    Console.WriteLine($"\n  File size: {fileSize:N0} bytes ({fileSize / 1024.0:F1} KB)");
}

// Cleanup
File.Delete("demo_measurement.omx");
Console.WriteLine("\nDone.");
