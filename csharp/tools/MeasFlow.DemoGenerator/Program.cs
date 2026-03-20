using MeasFlow;
using MeasFlow.Bus;

var outputPath = args.Length > 0 ? args[0] : "demo_measurement.meas";

// Also generate cross-language reference file
var refDir = Path.GetDirectoryName(outputPath) ?? ".";
var refPath = Path.Combine(refDir, "ref_csharp.meas");
CrossLanguageReferenceWriter.Write(refPath);
Console.WriteLine($"Cross-language reference: {refPath}");

using var writer = MeasFile.CreateWriter(outputPath);

// Group 1: Analog sensors
var motor = writer.AddGroup("Motor");
motor.Properties["Prüfstand"] = "P42";
motor.Properties["Operator"] = "Max Mustermann";
motor.Properties["TestStart"] = MeasTimestamp.Now;

var time = motor.AddChannel<MeasTimestamp>("Time");
var rpm = motor.AddChannel<float>("RPM");
var temp = motor.AddChannel<double>("OilTemperature");

rpm.Properties["Unit"] = "1/min";
temp.Properties["Unit"] = "°C";

var startTime = MeasTimestamp.Now;
var rng = new Random(42);
for (int i = 0; i < 1000; i++)
{
    time.Write(startTime + TimeSpan.FromMilliseconds(i));
    rpm.Write(3000.0f + (float)(Math.Sin(i * 0.01) * 500) + (float)(rng.NextDouble() * 10));
    temp.Write(92.0 + Math.Sin(i * 0.005) * 3.0);
}

// Group 2: CAN bus
var can = writer.AddBusGroup("CAN_Powertrain", new CanBusConfig { BaudRate = 500_000 });

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

for (int i = 0; i < 100; i++)
{
    var ts = startTime + TimeSpan.FromMilliseconds(i * 10);
    ushort rpmRaw = (ushort)((3000 + i * 10) / 0.25);
    can.WriteFrame(ts, 0x100, new byte[]
    {
        (byte)(rpmRaw & 0xFF), (byte)(rpmRaw >> 8),
        130, 0x00, 0x00, 0x00, 0x00, 0x00,
    });

    if (i % 5 == 0)
    {
        can.WriteFrame(ts, 0x200, new byte[]
        {
            0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        });
    }
}
