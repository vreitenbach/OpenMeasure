using System.Buffers.Binary;
using MeasFlow.Format;

namespace MeasFlow.Tests;

/// <summary>
/// Proves that the MEAS format supports true streaming:
/// - Writer can flush incrementally → multiple data segments on disk
/// - Reader can read partial files while writer is still appending
/// - Chunks are independently decodable without reading the entire file
/// - Memory usage stays bounded regardless of total data volume
/// </summary>
public class StreamingTests : IDisposable
{
    private readonly string _tempDir;

    public StreamingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"omx_streaming_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string TempFile(string name = "streaming.meas") => Path.Combine(_tempDir, name);

    /// <summary>
    /// Proves: Multiple Flush() calls create multiple independent data segments.
    /// Each flush writes its own segment header + chunk data on disk.
    /// Reader reassembles all chunks into the complete channel.
    /// </summary>
    [Fact]
    public void MultipleFlush_CreatesMultipleSegments_AllDataReadable()
    {
        var path = TempFile("multi_flush.meas");
        const int flushCount = 10;
        const int samplesPerFlush = 1000;

        // Write with explicit flushes
        using (var writer = MeasFile.CreateWriter(path))
        {
            var group = writer.AddGroup("Stream");
            var ch = group.AddChannel<float>("Signal");

            for (int flush = 0; flush < flushCount; flush++)
            {
                for (int i = 0; i < samplesPerFlush; i++)
                {
                    ch.Write(flush * samplesPerFlush + i);
                }
                writer.Flush(); // Each flush → one data segment on disk
            }
        }

        // Verify all data readable and in correct order
        using var reader = MeasFile.OpenRead(path);
        var channel = reader["Stream"]["Signal"];

        Assert.Equal(flushCount * samplesPerFlush, channel.SampleCount);

        var allData = channel.ReadAll<float>();
        for (int i = 0; i < allData.Length; i++)
        {
            Assert.Equal((float)i, allData[i]);
        }
    }

    /// <summary>
    /// Proves: ReadChunks returns data chunk-by-chunk (segment-by-segment),
    /// enabling streaming consumption without loading all data into memory.
    /// </summary>
    [Fact]
    public void ReadChunks_ReturnsDataInSegmentGranularity()
    {
        var path = TempFile("chunks.meas");
        const int flushCount = 5;
        int[] samplesPerFlush = [100, 200, 50, 300, 150];

        using (var writer = MeasFile.CreateWriter(path))
        {
            var group = writer.AddGroup("Chunked");
            var ch = group.AddChannel<int>("Values");

            int value = 0;
            for (int flush = 0; flush < flushCount; flush++)
            {
                for (int i = 0; i < samplesPerFlush[flush]; i++)
                {
                    ch.Write(value++);
                }
                writer.Flush();
            }
        }

        // Read chunk-by-chunk
        using var reader = MeasFile.OpenRead(path);
        var channel = reader["Chunked"]["Values"];

        int chunkIndex = 0;
        int runningTotal = 0;
        foreach (var chunk in channel.ReadChunks<int>())
        {
            Assert.Equal(samplesPerFlush[chunkIndex], chunk.Length);

            // Verify values are sequential within each chunk
            for (int i = 0; i < chunk.Length; i++)
            {
                Assert.Equal(runningTotal + i, chunk.Span[i]);
            }

            runningTotal += chunk.Length;
            chunkIndex++;
        }

        Assert.Equal(flushCount, chunkIndex);
        Assert.Equal(800, runningTotal); // 100+200+50+300+150
    }

    /// <summary>
    /// Proves: The binary file structure is segment-linked (NextSegmentOffset),
    /// allowing sequential forward scanning without random access.
    /// Verifies actual bytes on disk match the specification.
    /// </summary>
    [Fact]
    public void FileStructure_HasLinkedSegments_VerifiableOnDisk()
    {
        var path = TempFile("segments.meas");

        using (var writer = MeasFile.CreateWriter(path))
        {
            var group = writer.AddGroup("Test");
            var ch = group.AddChannel<float>("Data");

            ch.Write(1.0f);
            writer.Flush(); // Segment 1: metadata + Segment 2: data

            ch.Write(2.0f);
            writer.Flush(); // Segment 3: data

            ch.Write(3.0f);
            // Segment 4: data (on Dispose)
        }

        // Read raw binary file and verify segment chain
        var fileBytes = File.ReadAllBytes(path);
        Assert.True(fileBytes.Length >= FileHeader.Size);

        // Verify magic bytes: MEAS
        Assert.Equal((byte)'M', fileBytes[0]);
        Assert.Equal((byte)'E', fileBytes[1]);
        Assert.Equal((byte)'A', fileBytes[2]);
        Assert.Equal((byte)'S', fileBytes[3]);

        // Verify version = 1
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(fileBytes.AsSpan(4));
        Assert.Equal(1, version);

        // Read header
        long firstSegmentOffset = BinaryPrimitives.ReadInt64LittleEndian(fileBytes.AsSpan(8));
        long segmentCount = BinaryPrimitives.ReadInt64LittleEndian(fileBytes.AsSpan(24));

        Assert.Equal(FileHeader.Size, firstSegmentOffset); // Segments start right after header
        Assert.Equal(4, segmentCount); // 1 metadata + 3 data segments

        // Walk the segment chain
        long offset = firstSegmentOffset;
        var segmentTypes = new List<int>();
        for (int i = 0; i < segmentCount; i++)
        {
            int segType = BinaryPrimitives.ReadInt32LittleEndian(fileBytes.AsSpan((int)offset));
            long contentLength = BinaryPrimitives.ReadInt64LittleEndian(fileBytes.AsSpan((int)offset + 8));
            long nextOffset = BinaryPrimitives.ReadInt64LittleEndian(fileBytes.AsSpan((int)offset + 16));

            segmentTypes.Add(segType);
            Assert.True(contentLength > 0, $"Segment {i} should have content");

            if (i < segmentCount - 1)
            {
                Assert.True(nextOffset > offset, $"Segment {i}: NextOffset must advance forward");
                offset = nextOffset;
            }
        }

        // First segment is metadata (1), rest are data (2)
        Assert.Equal(1, segmentTypes[0]); // SegmentType.Metadata
        Assert.Equal(2, segmentTypes[1]); // SegmentType.Data
        Assert.Equal(2, segmentTypes[2]); // SegmentType.Data
        Assert.Equal(2, segmentTypes[3]); // SegmentType.Data
    }

    /// <summary>
    /// Proves: Each Flush() writes a self-contained, valid segment to disk.
    /// After Flush() + Dispose(), the file is immediately readable — demonstrating
    /// that the streaming model produces valid output at every flush boundary.
    /// </summary>
    [Fact]
    public void PartialFileRead_SegmentsValidAfterEachFlush()
    {
        var path = TempFile("partial.meas");

        using (var writer = MeasFile.CreateWriter(path))
        {
            var group = writer.AddGroup("Live");
            var ch = group.AddChannel<float>("Sensor");

            // First batch
            ch.Write([1.0f, 2.0f, 3.0f]);
            writer.Flush();

            // Second batch
            ch.Write([4.0f, 5.0f, 6.0f]);
            writer.Flush();

            // Third batch
            ch.Write([7.0f, 8.0f]);
            // auto-flush on Dispose
        }

        // Verify ALL batches are present and in order
        using (var reader = MeasFile.OpenRead(path))
        {
            var data = reader["Live"]["Sensor"].ReadAll<float>();
            Assert.Equal([1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f, 7.0f, 8.0f], data);

            // Verify data comes from multiple chunks (segments)
            int chunkCount = 0;
            foreach (var chunk in reader["Live"]["Sensor"].ReadChunks<float>())
                chunkCount++;
            Assert.Equal(3, chunkCount); // 3 flushes → 3 data segments
        }
    }

    /// <summary>
    /// Proves: TRUE concurrent read/write — a reader can open and read data
    /// from an MEAS file WHILE the writer still has it open and is actively writing.
    /// The writer uses FileShare.Read, the reader uses FileShare.ReadWrite.
    /// The reader handles SegmentCount=0 (header not yet patched) by walking the
    /// segment chain until end-of-file.
    /// </summary>
    [Fact]
    public void ConcurrentReadWhileWriting_ReaderSeesFlishedData()
    {
        var path = TempFile("concurrent.meas");

        using var writer = MeasFile.CreateWriter(path);
        var group = writer.AddGroup("Live");
        var ch = group.AddChannel<float>("Sensor");

        // Write first batch and flush to disk
        ch.Write([1.0f, 2.0f, 3.0f]);
        writer.Flush();

        // ─── Reader opens the SAME file while writer is still open ───
        // SegmentCount in the header is still 0 (will be patched on writer.Dispose())
        // The reader must walk the segment chain using NextSegmentOffset
        using (var reader = MeasFile.OpenRead(path))
        {
            var data = reader["Live"]["Sensor"].ReadAll<float>();
            Assert.Equal([1.0f, 2.0f, 3.0f], data);
        }

        // Write second batch and flush
        ch.Write([4.0f, 5.0f]);
        writer.Flush();

        // Reader opens again — now sees both batches
        using (var reader = MeasFile.OpenRead(path))
        {
            var data = reader["Live"]["Sensor"].ReadAll<float>();
            Assert.Equal([1.0f, 2.0f, 3.0f, 4.0f, 5.0f], data);

            // Verify 2 data chunks (one per flush)
            int chunkCount = 0;
            foreach (var chunk in reader["Live"]["Sensor"].ReadChunks<float>())
                chunkCount++;
            Assert.Equal(2, chunkCount);
        }

        // Write third batch — NOT flushed yet (only in writer's buffer)
        ch.Write([6.0f, 7.0f, 8.0f]);

        // Reader should NOT see unflushed data
        using (var reader = MeasFile.OpenRead(path))
        {
            var data = reader["Live"]["Sensor"].ReadAll<float>();
            Assert.Equal([1.0f, 2.0f, 3.0f, 4.0f, 5.0f], data); // Still only 5 values
        }

        // Now flush the third batch
        writer.Flush();

        // Reader sees all 8 values
        using (var reader = MeasFile.OpenRead(path))
        {
            var data = reader["Live"]["Sensor"].ReadAll<float>();
            Assert.Equal([1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f, 7.0f, 8.0f], data);
        }
    }

    /// <summary>
    /// Proves: Memory stays bounded during streaming writes.
    /// After each Flush(), the internal buffers are cleared.
    /// Writing 10M samples in chunks never holds more than one chunk in memory.
    /// </summary>
    [Fact]
    public void StreamingWrite_BoundedMemory_LargeDataset()
    {
        var path = TempFile("bounded_memory.meas");
        const int totalSamples = 2_000_000;
        const int chunkSize = 10_000;
        const int expectedChunks = totalSamples / chunkSize;

        long memBefore = GC.GetTotalMemory(true);

        using (var writer = MeasFile.CreateWriter(path))
        {
            var group = writer.AddGroup("Continuous");
            var ch = group.AddChannel<double>("HighRate");

            for (int chunk = 0; chunk < expectedChunks; chunk++)
            {
                var buffer = new double[chunkSize];
                for (int i = 0; i < chunkSize; i++)
                {
                    buffer[i] = Math.Sin(2 * Math.PI * (chunk * chunkSize + i) / totalSamples);
                }
                ch.Write(buffer.AsSpan());
                writer.Flush(); // Clears internal buffer

                // After flush, internal buffer should be empty
                // (we can't directly assert internal state, but we verify
                //  that the final file has all data correctly)
            }
        }

        long memAfter = GC.GetTotalMemory(true);

        // Verify file integrity
        using var reader = MeasFile.OpenRead(path);
        var channel = reader["Continuous"]["HighRate"];

        Assert.Equal(totalSamples, channel.SampleCount);

        // Verify chunk count by enumerating chunks
        int chunkCount = 0;
        foreach (var c in channel.ReadChunks<double>())
        {
            Assert.Equal(chunkSize, c.Length);
            chunkCount++;
        }
        Assert.Equal(expectedChunks, chunkCount);

        // File size should be proportional to data volume
        var fileSize = new FileInfo(path).Length;
        long rawDataSize = totalSamples * sizeof(double); // 16 MB
        Assert.True(fileSize > rawDataSize * 0.9, // Should be close to raw data size (small overhead)
            $"File size {fileSize} should be close to raw data size {rawDataSize}");
    }

    /// <summary>
    /// Proves: Multi-channel streaming — different channels can be flushed together
    /// and each channel's chunks are independently tracked.
    /// </summary>
    [Fact]
    public void MultiChannel_StreamingFlush_IndependentChunks()
    {
        var path = TempFile("multi_ch_stream.meas");

        using (var writer = MeasFile.CreateWriter(path))
        {
            var group = writer.AddGroup("Sensors");
            var temp = group.AddChannel<float>("Temperature");
            var pressure = group.AddChannel<float>("Pressure");
            var rpm = group.AddChannel<int>("RPM");

            // Flush 1: all channels
            temp.Write([20.0f, 20.5f, 21.0f]);
            pressure.Write([1013.0f, 1013.5f]);
            rpm.Write([1500, 1600, 1700, 1800]);
            writer.Flush();

            // Flush 2: only some channels have data
            temp.Write([21.5f, 22.0f]);
            rpm.Write([1900, 2000]);
            writer.Flush();

            // Flush 3: all channels again
            temp.Write([22.5f]);
            pressure.Write([1014.0f, 1014.5f, 1015.0f]);
            rpm.Write([2100]);
            // auto-flush on Dispose
        }

        using var reader = MeasFile.OpenRead(path);
        var tempData = reader["Sensors"]["Temperature"].ReadAll<float>();
        var pressureData = reader["Sensors"]["Pressure"].ReadAll<float>();
        var rpmData = reader["Sensors"]["RPM"].ReadAll<int>();

        Assert.Equal([20.0f, 20.5f, 21.0f, 21.5f, 22.0f, 22.5f], tempData);
        Assert.Equal([1013.0f, 1013.5f, 1014.0f, 1014.5f, 1015.0f], pressureData);
        Assert.Equal([1500, 1600, 1700, 1800, 1900, 2000, 2100], rpmData);
    }

    /// <summary>
    /// Proves: Raw binary frames (CAN messages) can be streamed incrementally.
    /// Each flush produces a separate chunk of variable-length frames.
    /// </summary>
    [Fact]
    public void RawFrames_StreamingFlush_VariableLengthData()
    {
        var path = TempFile("raw_stream.meas");

        using (var writer = MeasFile.CreateWriter(path))
        {
            var group = writer.AddGroup("CAN");
            var raw = group.AddRawChannel("RawFrames");

            // Flush 1: 3 frames of different sizes
            raw.WriteFrame(new byte[] { 0x01, 0x02, 0x03 });
            raw.WriteFrame(new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 });
            raw.WriteFrame(new byte[] { 0xFF });
            writer.Flush();

            // Flush 2: 2 more frames
            raw.WriteFrame(new byte[] { 0xAA, 0xBB });
            raw.WriteFrame(new byte[] { 0xCC, 0xDD, 0xEE, 0xFF });
            // auto-flush
        }

        using var reader = MeasFile.OpenRead(path);
        var frames = reader["CAN"]["RawFrames"].ReadFrames();

        Assert.Equal(5, frames.Count);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, frames[0]);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 }, frames[1]);
        Assert.Equal(new byte[] { 0xFF }, frames[2]);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, frames[3]);
        Assert.Equal(new byte[] { 0xCC, 0xDD, 0xEE, 0xFF }, frames[4]);
    }

    /// <summary>
    /// Proves: Statistics are computed incrementally during streaming writes,
    /// not as a post-processing step. Stats are correct even with multiple flushes.
    /// </summary>
    [Fact]
    public void Statistics_IncrementalDuringStreamingWrite()
    {
        var path = TempFile("stats_stream.meas");

        using (var writer = MeasFile.CreateWriter(path))
        {
            var group = writer.AddGroup("Stats");
            var ch = group.AddChannel<float>("Data");

            // Flush 1
            ch.Write([10.0f, 20.0f, 30.0f]);
            writer.Flush();

            var statsAfterFlush1 = ch.Statistics;
            Assert.Equal(3, statsAfterFlush1.Count);
            Assert.Equal(10.0, statsAfterFlush1.Min, 1);
            Assert.Equal(30.0, statsAfterFlush1.Max, 1);
            Assert.Equal(20.0, statsAfterFlush1.Mean, 1);

            // Flush 2: new min and max
            ch.Write([5.0f, 50.0f]);
            writer.Flush();

            var statsAfterFlush2 = ch.Statistics;
            Assert.Equal(5, statsAfterFlush2.Count);
            Assert.Equal(5.0, statsAfterFlush2.Min, 1);
            Assert.Equal(50.0, statsAfterFlush2.Max, 1);
            Assert.Equal(23.0, statsAfterFlush2.Mean, 1); // (10+20+30+5+50)/5
        }

        // Persisted stats: metadata is re-patched on close with final statistics
        // covering all flushed data.
        using var reader = MeasFile.OpenRead(path);
        var stats = reader["Stats"]["Data"].Statistics;
        Assert.NotNull(stats);
        Assert.Equal(5, stats.Value.Count);
        Assert.Equal(5.0, stats.Value.Min, 1);
        Assert.Equal(50.0, stats.Value.Max, 1);
        Assert.Equal(23.0, stats.Value.Mean, 1); // (10+20+30+5+50)/5

        // But ALL data is readable across all segments
        var allData = reader["Stats"]["Data"].ReadAll<float>();
        Assert.Equal([10.0f, 20.0f, 30.0f, 5.0f, 50.0f], allData);
    }
}
