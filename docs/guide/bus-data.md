# Bus Data Support

MeasFlow provides first-class support for automotive bus data, compatible with MDF4 specifications.

## Supported Bus Types

- **CAN 2.0A/B** - Controller Area Network (11-bit and 29-bit identifiers)
- **CAN-FD** - CAN with Flexible Data-Rate
- **LIN** - Local Interconnect Network
- **FlexRay** - High-speed automotive bus
- **Ethernet** - Automotive Ethernet (100BASE-T1, 1000BASE-T1)
- **MOST** - Media Oriented Systems Transport

## Architecture

```
BusChannelDefinition
 ├── BusConfig (CAN/CAN-FD/LIN/FlexRay/Ethernet/MOST)
 ├── Frames[]
 │    ├── FrameId, PayloadLength, Flags, Direction
 │    ├── Signals[] (direct signals when no PDU layer)
 │    └── Pdus[]
 │         ├── Signals[]
 │         ├── MultiplexConfig
 │         ├── E2EProtection
 │         ├── SecOcConfig
 │         └── ContainedPdus[] (AUTOSAR I-PDU Mux)
 └── ValueTables[]
```

## Recording Bus Data

### CAN Example

=== "C#"

    ```csharp
    using var writer = MeasFile.CreateWriter("can_trace.meas");

    // Configure CAN bus
    var canConfig = new CanBusConfig
    {
        BusType = CanBusType.CanFd,
        BaudRate = 500_000,
        DataBaudRate = 2_000_000
    };

    var busGroup = writer.AddBusGroup("CAN1", canConfig);

    // Define frames
    var engineFrame = busGroup.DefineCanFrame("Engine", 0x123, 8);
    engineFrame.Signals.Add(new SignalDefinition
    {
        Name = "RPM",
        StartBit = 0,
        BitLength = 16,
        ByteOrder = ByteOrder.Intel,
        Factor = 0.25,
        Offset = 0,
        Unit = "rpm"
    });
    engineFrame.Signals.Add(new SignalDefinition
    {
        Name = "Temperature",
        StartBit = 16,
        BitLength = 8,
        Factor = 1.0,
        Offset = -40,
        Unit = "°C"
    });

    // Write frames
    byte[] payload = new byte[8];
    while (recording)
    {
        // Get frame from CAN hardware
        var frame = ReadCanFrame();
        busGroup.WriteFrame(MeasTimestamp.Now, frame.Id, frame.Data);
        writer.Flush();
    }
    ```

=== "Python"

    ```python
    with meas.Writer("can_trace.meas") as writer:
        # Configure CAN bus
        can_config = meas.CanBusConfig(
            bus_type=meas.CanBusType.CanFd,
            baud_rate=500_000,
            data_baud_rate=2_000_000
        )

        bus_group = writer.add_bus_group("CAN1", can_config)

        # Define frames
        engine_frame = bus_group.define_can_frame("Engine", 0x123, 8)
        engine_frame.add_signal(
            name="RPM",
            start_bit=0,
            bit_length=16,
            byte_order="intel",
            factor=0.25,
            offset=0,
            unit="rpm"
        )

        # Write frames
        while recording:
            frame = read_can_frame()
            bus_group.write_frame(time.time_ns(), frame.id, frame.data)
            writer.flush()
    ```

## Signal Definitions

### Basic Signal Properties

| Property | Description |
|----------|-------------|
| **Name** | Signal identifier |
| **StartBit** | Bit position in frame (0-based) |
| **BitLength** | Number of bits |
| **ByteOrder** | `Intel` (little-endian) or `Motorola` (big-endian) |
| **Factor** | Scaling factor (physical = raw × factor + offset) |
| **Offset** | Offset value |
| **Unit** | Physical unit (e.g., "rpm", "°C") |
| **ValueTable** | Enum mappings (0 → "Off", 1 → "On") |

### Value Tables

Map raw values to human-readable strings:

```csharp
var gearSignal = new SignalDefinition
{
    Name = "Gear",
    StartBit = 24,
    BitLength = 3,
    ValueTable = "GearStates"
};

busGroup.ValueTables.Add(new ValueTable
{
    Name = "GearStates",
    Entries = new Dictionary<long, string>
    {
        { 0, "Neutral" },
        { 1, "First" },
        { 2, "Second" },
        { 3, "Third" },
        { 4, "Fourth" },
        { 5, "Fifth" }
    }
});
```

## PDU Layer (AUTOSAR)

### I-PDU Support

Protocol Data Units represent logical message groups:

```csharp
var pdu = frame.AddPdu(new PduDefinition
{
    Name = "EnginePDU",
    StartByte = 0,
    Length = 8
});

pdu.Signals.Add(new SignalDefinition
{
    Name = "RPM",
    StartBit = 0,
    BitLength = 16,
    // ... signal properties
});
```

### Container-PDU

Multiple I-PDUs multiplexed in one frame:

```csharp
var containerPdu = frame.AddPdu(new PduDefinition
{
    Name = "Container",
    StartByte = 0,
    Length = 64,
    IsContainer = true
});

containerPdu.ContainedPdus.Add(new ContainedPduDefinition
{
    Name = "PDU_A",
    HeaderId = 0x01,
    PayloadLength = 16
});

containerPdu.ContainedPdus.Add(new ContainedPduDefinition
{
    Name = "PDU_B",
    HeaderId = 0x02,
    PayloadLength = 32
});
```

## Multiplexing

### MUX Signals

Signals whose presence depends on multiplexer values:

```csharp
var muxSignal = new SignalDefinition
{
    Name = "MuxSelector",
    StartBit = 0,
    BitLength = 8,
    IsMultiplexer = true
};

var signal_A = new SignalDefinition
{
    Name = "Value_A",
    StartBit = 8,
    BitLength = 16,
    MultiplexValue = 0  // Present when MuxSelector == 0
};

var signal_B = new SignalDefinition
{
    Name = "Value_B",
    StartBit = 8,
    BitLength = 16,
    MultiplexValue = 1  // Present when MuxSelector == 1
};
```

### Nested Multiplexing

Multiple levels of multiplexing:

```csharp
pdu.MultiplexConfig = new MultiplexConfig
{
    Type = MultiplexType.ValueBased,
    SwitchSignal = "Level1_Mux",
    NestedLevels = new[]
    {
        new MultiplexLevel
        {
            SwitchSignal = "Level2_Mux",
            Conditions = new[] { 0, 1, 2 }
        }
    }
};
```

## E2E Protection

AUTOSAR End-to-End protection for data integrity:

```csharp
pdu.E2EProtection = new E2EProtectionConfig
{
    Profile = E2EProfile.Profile01,
    DataId = 0x1234,
    CrcOffset = 0,    // CRC at byte 0
    CounterOffset = 1  // Counter at byte 1
};

// Supported profiles: 01-11
```

## SecOC (Secure Onboard Communication)

Authentication and encryption:

```csharp
pdu.SecOcConfig = new SecOcConfig
{
    AuthAlgorithm = SecOcAuthAlgorithm.CmacAes,
    AuthTxBitLength = 64,
    AuthRxBitLength = 64,
    FreshnessValueLength = 64,
    FreshnessValueTxBitLength = 24,
    FreshnessValueRxBitLength = 24
};
```

## Signal Decoding

Extract physical values from raw frames:

=== "C#"

    ```csharp
    using var reader = MeasFile.OpenRead("can_trace.meas");
    var busGroup = reader["CAN1"];

    // Decode single signal
    var rpm = busGroup.DecodeSignal("RPM");
    Console.WriteLine($"Average RPM: {rpm.ToArray().Average()}");

    // Decode all signals in frame
    var engineSignals = busGroup.DecodeFrame("Engine");
    foreach (var signal in engineSignals)
    {
        Console.WriteLine($"{signal.Key}: {signal.Value.ToArray().Average()}");
    }
    ```

=== "Python"

    ```python
    with meas.Reader("can_trace.meas") as reader:
        bus_group = reader["CAN1"]

        # Decode single signal
        rpm = bus_group.decode_signal("RPM")
        print(f"Average RPM: {rpm.mean()}")

        # Decode all signals in frame
        engine_signals = bus_group.decode_frame("Engine")
        for name, values in engine_signals.items():
            print(f"{name}: {values.mean()}")
    ```

## Raw Frame Wire Format

Each bus type has a specific binary layout:

### CAN/CAN-FD

```
[uint32 arbId] [byte dlc] [byte flags] [payload]
```

- **arbId**: 29-bit CAN identifier (extended format)
- **dlc**: Data Length Code
- **flags**: CAN-FD flags (BRS, ESI, etc.)

### LIN

```
[byte frameId] [byte dlc] [byte nad] [byte checksum] [payload]
```

- **frameId**: LIN frame identifier
- **nad**: Node Address
- **checksum**: LIN checksum

### FlexRay

```
[uint16 slotId] [byte cycle] [byte flags] [uint16 len] [payload]
```

- **slotId**: FlexRay slot identifier
- **cycle**: Cycle counter
- **flags**: Channel A/B, startup, etc.

### Ethernet

```
[6B macDst] [6B macSrc] [uint16 etherType] [uint16 vlan] [uint16 len] [payload]
```

- **macDst/macSrc**: MAC addresses
- **etherType**: Ethernet type/length
- **vlan**: VLAN tag (optional)

## Common Use Cases

### Automotive Test Automation

Record CAN traffic during vehicle tests:

```csharp
// Record
using var writer = MeasFile.CreateWriter("drive_cycle.meas");
var can = writer.AddBusGroup("CAN_Powertrain", canConfig);
// ... define frames and signals
RecordTestDrive(can);

// Analyze
using var reader = MeasFile.OpenRead("drive_cycle.meas");
var rpm = reader["CAN_Powertrain"].DecodeSignal("RPM");
var maxRpm = rpm.ToArray().Max();
Console.WriteLine($"Max RPM: {maxRpm}");
```

### Signal Validation

Verify signal ranges and patterns:

```csharp
var speed = busGroup.DecodeSignal("VehicleSpeed");
if (speed.ToArray().Any(s => s > 200))
{
    Console.WriteLine("Warning: Unrealistic speed detected");
}
```

### Cross-Language Analysis

Record with C, analyze with Python:

```c
// Record in C
meas_writer_t* w = meas_writer_open("recording.meas");
// ... record CAN frames
```

```python
# Analyze in Python
with meas.Reader("recording.meas") as reader:
    rpm = reader["CAN1"].decode_signal("RPM")
    plt.plot(rpm)
    plt.show()
```

## See Also

- [Streaming Architecture](streaming.md)
- [File Format Specification](file-format.md)
- [API Reference](../api/csharp.md)
- [AUTOSAR Specifications](https://www.autosar.org/)
