using OpenMeasure;

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

    // Group 2: CAN bus raw data with decoded signal
    var canGroup = writer.AddGroup("CAN_Powertrain");
    canGroup.Properties["BusType"] = "CAN 2.0B";
    canGroup.Properties["Baudrate"] = 500000;

    var canTime = canGroup.AddChannel<OmxTimestamp>("Timestamp");
    var rawFrames = canGroup.AddRawChannel("RawFrames");

    // Decoded signal linked to raw data
    var canRpm = canGroup.AddSignalChannel<float>(
        name: "EngineRPM",
        sourceChannelName: "RawFrames",
        startBit: 0, bitLength: 16,
        factor: 0.25, offset: 0.0);
    canRpm.Properties["Unit"] = "1/min";
    canRpm.Properties["CanId"] = 0x100;

    for (int i = 0; i < 100; i++)
    {
        canTime.Write(startTime + TimeSpan.FromMilliseconds(i * 10));

        // Raw CAN frame: [ID_lo, ID_hi, DLC, data...]
        ushort rpmRaw = (ushort)((3000 + i * 10) * 4); // 0.25 factor
        rawFrames.WriteFrame(new byte[]
        {
            0x00, 0x01, 8,
            (byte)(rpmRaw & 0xFF), (byte)(rpmRaw >> 8),
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        });

        canRpm.Write(rpmRaw * 0.25f);
    }

    Console.WriteLine("  Written: Motor group (1000 samples @ 1kHz)");
    Console.WriteLine("  Written: CAN group (100 frames with decoded RPM signal)");
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
        foreach (var (key, value) in group.Properties)
            Console.WriteLine($"    {key} = {value}");

        foreach (var channel in group.Channels)
        {
            Console.Write($"    Channel: {channel.Name} [{channel.DataType}]");
            Console.Write($" ({channel.SampleCount} samples)");
            if (channel.SourceChannelName != null)
                Console.Write($" ← decoded from '{channel.SourceChannelName}'");
            Console.WriteLine();
        }
    }

    // Read and display some values
    var rpmData = reader["Motor"]["RPM"].ReadAll<float>();
    var timeData = reader["Motor"]["Time"].ReadAll<OmxTimestamp>();
    Console.WriteLine($"\n  Motor RPM: first={rpmData[0]:F1}, last={rpmData[^1]:F1}");
    Console.WriteLine($"  Time range: {timeData[0]} → {timeData[^1]}");
    Console.WriteLine($"  Duration: {(timeData[^1] - timeData[0]).TotalMilliseconds:F1} ms");

    // CAN decoded signal
    var canRpmData = reader["CAN_Powertrain"]["EngineRPM"].ReadAll<float>();
    Console.WriteLine($"\n  CAN RPM signal: first={canRpmData[0]:F1}, last={canRpmData[^1]:F1}");

    var fileSize = new FileInfo("demo_measurement.omx").Length;
    Console.WriteLine($"\n  File size: {fileSize:N0} bytes ({fileSize / 1024.0:F1} KB)");
}

// Cleanup
File.Delete("demo_measurement.omx");
Console.WriteLine("\nDone.");
