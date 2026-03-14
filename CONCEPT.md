# MeasFlow (.meas) — Offene Messdaten-Bibliothek

## Konzept für eine hochperformante Messdaten-Bibliothek in C# / .NET 10

---

## 1. Problemanalyse bestehender Formate

| Format | Stärken | Schwächen |
|--------|---------|-----------|
| **TDMS** | Einfaches API, Streaming, hierarchisch | Proprietär (NI), kein offizieller .NET-Support, keine Kompression |
| **HDF5** | Extrem flexibel, komprimierbar | C-basiert, komplexes API, Interop-Overhead, Thread-Safety-Probleme |
| **MDF4** | Automotive-Standard, signalbasiert | Extrem komplexe Spezifikation, kaum nutzbare Open-Source-Libs |

### Gemeinsame Defizite
- Keine native .NET-Bibliothek mit Zero-Copy-Performance
- Kein Span<T>/Memory<T>-Support
- Kein async/await-Support
- Keine Source-Generator-Integration
- Lizenzprobleme oder proprietäre Abhängigkeiten

---

## 2. Design-Ziele

```
Einfachheit         ████████████████████  (wie TDMS)
Performance         ████████████████████  (wie HDF5)
Offenheit           ████████████████████  (MIT-Lizenz)
.NET-Integration    ████████████████████  (native C#, kein Interop)
```

### Kernprinzipien
1. **TDMS-ähnliche Einfachheit** — File → Group → Channel Hierarchie
2. **Zero-Copy wo möglich** — Memory-Mapped I/O, Span<T>-basiert
3. **Streaming-first** — Append-only Schreiben, sofort lesbar
4. **Columnar Storage** — Schneller Zugriff auf einzelne Kanäle
5. **100% Managed Code** — Kein P/Invoke, kein nativer Code
6. **MIT-Lizenz** — Keine Einschränkungen

---

## 3. Datenmodell

```
MeasFile
 ├── Properties: Dictionary<string, MeasValue>
 ├── Groups[]
 │    ├── Name: string
 │    ├── Properties: Dictionary<string, MeasValue>
 │    └── Channels[]
 │         ├── Name: string
 │         ├── DataType: MeasDataType
 │         ├── Properties: Dictionary<string, MeasValue>
 │         └── Data: T[]  (typisiert)
 └── Metadata (automatisch)
      ├── CreatedAt: DateTime
      ├── WriterVersion: string
      └── Endianness: Little
```

### Unterstützte Datentypen

```csharp
public enum MeasDataType : byte
{
    // Ganzzahlen
    Int8    = 0x01,  Int16  = 0x02,  Int32  = 0x03,  Int64  = 0x04,
    UInt8   = 0x05,  UInt16 = 0x06,  UInt32 = 0x07,  UInt64 = 0x08,

    // Fließkomma
    Float32  = 0x10,  Float64 = 0x11,  Float128 = 0x12,

    // Zeitstempel
    Timestamp = 0x20,    // int64 Ticks (100ns seit Unix-Epoch)
    TimeSpan  = 0x21,    // int64 Ticks

    // Text & Binär
    Utf8String = 0x30,
    Binary     = 0x31,

    // Spezial
    Complex64  = 0x40,   // 2x Float32
    Complex128 = 0x41,   // 2x Float64
    Bool       = 0x50,
}
```

---

## 4. Binäres Dateiformat (.meas)

### 4.1 Dateistruktur — Segment-basiert

```
┌─────────────────────────────────────────────────┐
│  File Header (64 Bytes, fix)                    │
├─────────────────────────────────────────────────┤
│  Segment 1: Metadata                            │
│    ├── File Properties                          │
│    ├── Group Definitions                        │
│    └── Channel Definitions + Properties         │
├─────────────────────────────────────────────────┤
│  Segment 2: Data Block                          │
│    ├── Segment Header                           │
│    ├── Channel 0 Chunk (N Samples)              │
│    ├── Channel 1 Chunk (N Samples)              │
│    └── Channel 2 Chunk (N Samples)              │
├─────────────────────────────────────────────────┤
│  Segment 3: Data Block                          │
│    ├── Segment Header                           │
│    ├── Channel 0 Chunk (N Samples)              │
│    └── Channel 2 Chunk (N Samples, sparse OK)   │
├─────────────────────────────────────────────────┤
│  ...weitere Segmente...                         │
├─────────────────────────────────────────────────┤
│  File Footer / Index (optional)                 │
│    ├── Segment-Offset-Tabelle                   │
│    ├── Channel-Offset-Index                     │
│    └── Summary Statistics (Min/Max/Count)        │
└─────────────────────────────────────────────────┘
```

### 4.2 File Header (64 Bytes)

```
Offset  Größe  Beschreibung
------  -----  --------------------------------
0x00    4      Magic Number: "MEAS\0" (0x4D454153)
0x04    2      Format-Version: Major.Minor (uint16)
0x06    2      Flags (Kompression, Endianness, Index vorhanden)
0x08    8      Offset zum ersten Segment (int64)
0x10    8      Offset zum Footer/Index (int64, 0 = kein Index)
0x18    8      Gesamtzahl Segmente (int64)
0x20    16     File-UUID (Guid)
0x30    8      Erstellungszeitpunkt (int64, Ticks)
0x38    8      Reserviert / Padding
```

### 4.3 Segment Header (32 Bytes)

```
Offset  Größe  Beschreibung
------  -----  --------------------------------
0x00    4      Segment-Typ: Metadata=1, Data=2, Index=3
0x04    4      Flags (Kompression pro Segment)
0x08    8      Segment-Länge in Bytes (int64)
0x10    8      Offset zum nächsten Segment (int64, 0 = letztes)
0x18    4      Anzahl Chunks in diesem Segment
0x1C    4      CRC32 des Segment-Inhalts
```

### 4.4 Kompression

```
Flag  Algorithmus    Einsatz
----  -------------  ----------------------------------
0x00  None           Maximale Schreibgeschwindigkeit
0x01  LZ4            Standard — sehr schnell, moderate Ratio
0x02  Zstd           Hohe Ratio, etwas langsamer
0x03  Delta+LZ4      Zeitreihen — Delta-Encoding + LZ4
```

Delta-Encoding für monotone Zeitreihen:
```
Original:  [1000, 1001, 1002, 1003, 1005, 1006]
Delta:     [1000,    1,    1,    1,    2,    1]  → komprimiert deutlich besser
```

---

## 5. API-Design

### 5.1 Schreiben — Streaming

```csharp
// Einfachster Fall: Datei schreiben
using var writer = MeasFile.CreateWriter("messung.meas");

var group = writer.AddGroup("Motor");
group.Properties["Prüfstand"] = "P42";

var rpm = group.AddChannel<float>("RPM");
var temp = group.AddChannel<double>("Temperatur");
var time = group.AddChannel<MeasTimestamp>("Zeit");

// Daten schreiben — einzeln oder als Block
rpm.Write(1500.0f);
temp.Write(85.3);
time.Write(MeasTimestamp.Now);

// Block-Schreiben für Performance
float[] rpmBlock = acquisitionSystem.ReadBuffer();
rpm.Write(rpmBlock.AsSpan());

// Flush erzwingt Segment-Schreiben auf Disk
writer.Flush();

// Dispose schließt die Datei und schreibt den Index
```

### 5.2 Schreiben — Bulk / High-Performance

```csharp
using var writer = MeasFile.CreateWriter("messung.meas", new MeasWriterOptions
{
    Compression = MeasCompression.DeltaLz4,
    SegmentSize = 64 * 1024 * 1024,  // 64 MB Segmente
    BufferSize  = 4 * 1024 * 1024,   // 4 MB Write-Buffer
});

var group = writer.AddGroup("Vibration");
var accel = group.AddChannel<float>("Beschleunigung_X");

// Zero-Copy Schreiben mit IBufferWriter<T>
var buffer = accel.GetWriteBuffer(10_000);
acquisitionSystem.FillBuffer(buffer.Span);
accel.Advance(10_000);

// Async Schreiben
await accel.WriteAsync(hugeDataBlock, cancellationToken);
```

### 5.3 Lesen — Wahlfreier Zugriff

```csharp
// Datei öffnen (Memory-Mapped, kein vollständiges Laden)
using var file = MeasFile.OpenRead("messung.meas");

// Struktur erkunden
foreach (var group in file.Groups)
{
    Console.WriteLine($"Group: {group.Name}");
    foreach (var channel in group.Channels)
    {
        Console.WriteLine($"  Channel: {channel.Name} [{channel.DataType}] " +
                          $"Samples: {channel.SampleCount}");
    }
}

// Kanal lesen — typisiert
var rpm = file["Motor"]["RPM"].AsFloat32();

// Gesamte Daten als Span (Zero-Copy bei unkomprimierten Daten)
ReadOnlySpan<float> allRpm = rpm.ReadAll();

// Bereich lesen
ReadOnlySpan<float> segment = rpm.Read(startIndex: 1000, count: 5000);

// Streaming-Lesen für große Kanäle
await foreach (ReadOnlyMemory<float> chunk in rpm.ReadChunksAsync())
{
    ProcessChunk(chunk);
}
```

### 5.4 Lesen — LINQ-ähnliche Abfragen

```csharp
// Statistiken (aus Index, ohne Daten zu lesen)
var stats = file["Motor"]["RPM"].Statistics;
Console.WriteLine($"Min: {stats.Min}, Max: {stats.Max}, Mean: {stats.Mean}");

// Zeitbasierter Zugriff (wenn Zeitkanal vorhanden)
var timeRange = file["Motor"].TimeSlice(
    from: MeasTimestamp.Parse("2026-03-13T10:00:00"),
    to:   MeasTimestamp.Parse("2026-03-13T10:05:00")
);

float[] rpmInRange = timeRange["RPM"].AsFloat32().ReadAll().ToArray();
```

### 5.5 Konvertierung

```csharp
// CSV-Export
await MeasConvert.ToCsvAsync(file["Motor"], "export.csv");

// Pandas-kompatibel (Arrow IPC)
await MeasConvert.ToArrowAsync(file["Motor"], "export.arrow");

// TDMS-Import
using var tdmsFile = TdmsImporter.Open("legacy.tdms");
using var writer = MeasFile.CreateWriter("converted.meas");
await MeasConvert.FromTdms(tdmsFile, writer);
```

---

## 6. Performance-Architektur

### 6.1 Schreib-Pipeline

```
Anwendung                  Bibliothek
─────────                  ──────────
Write(Span<T>) ──────────► Ring-Buffer (lock-free)
                                │
                           Hintergrund-Thread
                                │
                           ┌────▼────┐
                           │ Delta   │  (optional)
                           │ Encode  │
                           └────┬────┘
                                │
                           ┌────▼────┐
                           │ LZ4     │  (optional)
                           │ Compress│
                           └────┬────┘
                                │
                           ┌────▼────┐
                           │ Segment │
                           │ Write   │──────► Disk (FileStream / mmap)
                           └─────────┘
```

### 6.2 Lese-Pipeline

```
                           Bibliothek
                           ──────────
Disk ──────────────────► Memory-Mapped File
                                │
                           ┌────▼────┐
                           │ Segment │
                           │ Locate  │  (via Index → O(1))
                           └────┬────┘
                                │
                           ┌────▼────┐
                           │ Decompr.│  (lazy, on-demand)
                           └────┬────┘
                                │
ReadOnlySpan<T> ◄───────── Zero-Copy View
```

### 6.3 Performance-Ziele

| Operation | Ziel | Vergleich |
|-----------|------|-----------|
| Sequentielles Schreiben | > 2 GB/s (unkomprimiert) | TDMS: ~800 MB/s |
| Schreiben mit LZ4 | > 1.2 GB/s | HDF5+gzip: ~200 MB/s |
| Sequentielles Lesen | > 3 GB/s (mmap, unkomprimiert) | TDMS: ~1 GB/s |
| Einzelkanal-Zugriff | O(1) Seek + sequentiell | HDF5: O(1), TDMS: O(n) |
| Datei öffnen (Metadaten) | < 1 ms (mit Index) | HDF5: 10-100 ms |
| Speicher-Overhead | < 64 KB pro Writer | — |

### 6.4 Technische Mittel

```csharp
// 1. Span<T> und Memory<T> durchgängig
public void Write(ReadOnlySpan<float> data);
public ReadOnlySpan<float> ReadAll();

// 2. Memory-Mapped Files für Lesen
private readonly MemoryMappedFile _mmf;
private readonly MemoryMappedViewAccessor _view;

// 3. IBufferWriter<T> für Zero-Alloc Schreiben
public IBufferWriter<T> GetWriteBuffer(int sizeHint);

// 4. SIMD für Delta-Encoding / Statistiken
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private static void DeltaEncode(Span<int> data)
{
    if (Vector.IsHardwareAccelerated && data.Length >= Vector<int>.Count)
    {
        // SIMD-beschleunigtes Delta-Encoding
    }
}

// 5. ArrayPool<T> für temporäre Puffer
private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;

// 6. Channel<T> für Producer/Consumer-Pipeline
private readonly Channel<DataSegment> _writeChannel;

// 7. Source Generators für Serialisierung
[MeasSerializable]
public partial record SensorReading(float X, float Y, float Z, long Timestamp);
```

---

## 7. Paket-Struktur (NuGet)

```
MeasFlow/
├── src/
│   ├── MeasFlow/                     # Kern-Bibliothek
│   │   ├── MeasFile.cs                   # Haupt-Einstiegspunkt
│   │   ├── MeasWriter.cs                 # Streaming Writer
│   │   ├── MeasReader.cs                 # Memory-Mapped Reader
│   │   ├── MeasChannel.cs                # Kanal-Abstraktion
│   │   ├── MeasGroup.cs                  # Gruppen-Abstraktion
│   │   ├── Format/
│   │   │   ├── FileHeader.cs
│   │   │   ├── SegmentHeader.cs
│   │   │   ├── MetadataEncoder.cs
│   │   │   └── IndexBuilder.cs
│   │   ├── Compression/
│   │   │   ├── ICompressor.cs
│   │   │   ├── Lz4Compressor.cs
│   │   │   ├── ZstdCompressor.cs
│   │   │   └── DeltaEncoder.cs
│   │   ├── IO/
│   │   │   ├── MemoryMappedReader.cs
│   │   │   ├── StreamingWriter.cs
│   │   │   └── RingBuffer.cs
│   │   └── Statistics/
│   │       └── ChannelStatistics.cs
│   │
│   ├── MeasFlow.Generators/          # Source Generators
│   │   └── MeasSerializableGenerator.cs
│   │
│   └── MeasFlow.Converters/          # Import/Export
│       ├── CsvConverter.cs
│       ├── ArrowConverter.cs
│       └── TdmsImporter.cs
│
├── tests/
│   ├── MeasFlow.Tests/
│   └── MeasFlow.Benchmarks/          # BenchmarkDotNet
│
├── samples/
│   ├── QuickStart/
│   ├── HighPerformanceWriter/
│   └── DataAnalysis/
│
├── docs/
│   └── FORMAT_SPEC.md                   # Formale Format-Spezifikation
│
├── LICENSE                              # MIT
└── README.md
```

### NuGet-Pakete

| Paket | Inhalt | Abhängigkeiten |
|-------|--------|----------------|
| `MeasFlow` | Kern-Bibliothek | keine (!) |
| `MeasFlow.Generators` | Source-Gen für struct-Serialisierung | Roslyn |
| `MeasFlow.Compression.Lz4` | LZ4-Kompression | K4os.Compression.LZ4 |
| `MeasFlow.Compression.Zstd` | Zstd-Kompression | ZstdSharp |
| `MeasFlow.Converters` | CSV, Arrow, TDMS-Import | Apache.Arrow |

**Kern-Bibliothek hat null externe Abhängigkeiten.**

---

## 8. Vergleich mit bestehenden Formaten

```
                    TDMS    HDF5    MDF4    MEAS (dieses Konzept)
                    ────    ────    ────    ────────────────────
Lizenz              Propr.  BSD*    Propr.  MIT
.NET nativ          ✗       ✗       ✗       ✓
Zero-Copy Lesen     ✗       ✗       ✗       ✓ (mmap + Span<T>)
Async/Await         ✗       ✗       ✗       ✓
Streaming Write     ✓       ✗       ✓       ✓
Kompression         ✗       ✓       ✗       ✓ (LZ4, Zstd, Delta)
SIMD-Beschleunigung ✗       ✗       ✗       ✓
Source Generators   ✗       ✗       ✗       ✓
Einfaches API       ✓       ✗       ✗       ✓
Columnar Storage    ~       ✓       ~       ✓
Index/Schnellzugriff ✗      ✓       ✓       ✓
Sparse Channels     ✗       ✓       ✓       ✓
Thread-Safe         ✗       ✗       ✗       ✓ (lock-free Writer)

* HDF5: BSD-Lizenz, aber .NET-Wrapper haben eigene Lizenzprobleme
```

---

## 9. Erweiterbarkeit

### 9.1 Custom Kompression
```csharp
public interface IMeasCompressor
{
    MeasCompression Id { get; }
    int Compress(ReadOnlySpan<byte> source, Span<byte> destination);
    int Decompress(ReadOnlySpan<byte> source, Span<byte> destination);
}

// Registrierung
MeasCompression.Register(0x10, new MyCustomCompressor());
```

### 9.2 Custom Datentypen
```csharp
[MeasSerializable]
public partial record GpsCoordinate(double Lat, double Lon, double Alt);

// Source Generator erzeugt automatisch Serializer
var channel = group.AddChannel<GpsCoordinate>("Position");
channel.Write(new GpsCoordinate(48.1351, 11.5820, 519.0));
```

### 9.3 Events / Marker
```csharp
// Zeitbasierte Marker innerhalb einer Gruppe
group.AddMarker("Motorstart", MeasTimestamp.Now, new {
    RPM = 800.0f,
    Status = "OK"
});
```

---

## 10. Implementierungs-Roadmap

### Phase 1: Kern (MVP)
- [ ] File Header lesen/schreiben
- [ ] Metadata-Segmente (Groups, Channels, Properties)
- [ ] Data-Segmente schreiben (unkomprimiert)
- [ ] Data-Segmente lesen (Memory-Mapped)
- [ ] Grundlegende Datentypen (float, double, int, timestamp)
- [ ] Unit Tests + Benchmarks

### Phase 2: Performance
- [ ] LZ4-Kompression
- [ ] Delta-Encoding für Zeitreihen
- [ ] Ring-Buffer Write-Pipeline
- [ ] SIMD-beschleunigte Operationen
- [ ] Index/Footer für schnellen Zugriff
- [ ] Channel-Statistiken (Min/Max/Mean)

### Phase 3: Ergonomie
- [ ] Source Generators für Custom-Typen
- [ ] Async Read/Write APIs
- [ ] CSV/Arrow-Export
- [ ] TDMS-Import
- [ ] Time-Slice-Abfragen
- [ ] Sparse Channels

### Phase 4: Ökosystem
- [ ] Python-Reader (via Format-Spezifikation)
- [ ] CLI-Tool (`MEAS inspect`, `MEAS convert`)
- [ ] VS2026-Visualizer
- [ ] Format-Spezifikation als eigenständiges Dokument
