using System.Diagnostics;
using System.Runtime.InteropServices;
using MeasFlow;
using PureHDF;

namespace MeasFlow.Benchmarks;

/// <summary>
/// Lightweight CI benchmark runner that outputs the same plain-text format
/// as the C and Python benchmark suites, making report generation trivial.
///
/// Usage:  dotnet run -c Release -- --ci
/// For detailed local analysis, omit --ci to use BenchmarkDotNet.
/// </summary>
public static class CiRunner
{
    private const int Warmup = 1;
    private const int Iterations = 5;

    public static void Run()
    {
        RunCrossLanguage();
        RunFormatComparison();
    }

    // ── Cross-language benchmarks ────────────────────────────────────────

    private static void RunCrossLanguage()
    {
        int[] sampleCounts = [100_000, 1_000_000];

        foreach (var n in sampleCounts)
        {
            var data = GenerateData(n);
            var tmpDir = CreateTempDir("ci_xlang");

            try
            {
                Console.WriteLine();
                Console.WriteLine(new string('=', 60));
                Console.WriteLine($"  Cross-language benchmarks (C#) -- {n} samples");
                Console.WriteLine(new string('=', 60));

                // Write benchmark
                var path = Path.Combine(tmpDir, "write.meas");
                var r = Bench(() =>
                {
                    using var writer = MeasFile.CreateWriter(path);
                    var group = writer.AddGroup("Data");
                    var ch = group.AddChannel<float>("Signal");
                    ch.Write(data.AsSpan());
                });
                Console.WriteLine($"\n  Write (C#):        {r.MedianMs,8:F2} ms");

                // Streaming write
                r = Bench(() =>
                {
                    var p = Path.Combine(tmpDir, "stream.meas");
                    using var writer = MeasFile.CreateWriter(p);
                    var group = writer.AddGroup("Data");
                    var ch = group.AddChannel<float>("Signal");
                    int chunk = n / 10;
                    for (int i = 0; i < 10; i++)
                    {
                        ch.Write(data.AsSpan(i * chunk, chunk));
                        writer.Flush();
                    }
                });
                Console.WriteLine($"  Stream (C#):       {r.MedianMs,8:F2} ms  (10 flushes)");

                // Read benchmark
                using (var writer = MeasFile.CreateWriter(path))
                {
                    var group = writer.AddGroup("Data");
                    var ch = group.AddChannel<float>("Signal");
                    ch.Write(data.AsSpan());
                }
                r = Bench(() =>
                {
                    using var reader = MeasFile.OpenRead(path);
                    reader["Data"]["Signal"].ReadAll<float>();
                });
                Console.WriteLine($"  Read (C#):         {r.MedianMs,8:F2} ms");

                // File size
                var sizeKb = new FileInfo(path).Length / 1024.0;
                var rawKb = n * 4.0 / 1024.0;
                var overhead = (sizeKb - rawKb) / rawKb * 100;
                Console.WriteLine($"\n  File size:         {sizeKb,8:F1} KB  (overhead: {overhead:F1}% vs raw)");
            }
            finally
            {
                Directory.Delete(tmpDir, true);
            }
        }
    }

    // ── Format comparison benchmarks ─────────────────────────────────────

    private static void RunFormatComparison()
    {
        int[] sampleCounts = [100_000, 1_000_000];

        foreach (var n in sampleCounts)
        {
            var data = GenerateData(n);
            var tmpDir = CreateTempDir("ci_fmtcmp");

            try
            {
                Console.WriteLine();
                Console.WriteLine(new string('=', 60));
                Console.WriteLine($"  Format comparison (C#) -- {n} samples");
                Console.WriteLine(new string('=', 60));

                // ── Write 1 channel ──
                PrintSection("Write 1 channel");
                var measPath = Path.Combine(tmpDir, "w1.meas");
                var r = Bench(() => WriteMeasFlow(measPath, data));
                Console.WriteLine($"  MeasFlow:          {r.MedianMs,8:F2} ms");

                var h5Path = Path.Combine(tmpDir, "w1.h5");
                r = Bench(() => WriteHdf5(h5Path, data));
                Console.WriteLine($"  HDF5 (PureHDF):    {r.MedianMs,8:F2} ms");

                // ── Write 10 channels ──
                PrintSection("Write 10 channels");
                r = Bench(() =>
                {
                    var p = Path.Combine(tmpDir, "w10.meas");
                    using var writer = MeasFile.CreateWriter(p);
                    var group = writer.AddGroup("Data");
                    for (int c = 0; c < 10; c++)
                    {
                        var ch = group.AddChannel<float>($"Ch{c}", trackStatistics: false);
                        ch.Write(data.AsSpan());
                    }
                });
                Console.WriteLine($"  MeasFlow:          {r.MedianMs,8:F2} ms");

                r = Bench(() =>
                {
                    var p = Path.Combine(tmpDir, "w10.h5");
                    var group = new H5Group();
                    for (int c = 0; c < 10; c++)
                        group[$"Ch{c}"] = data;
                    new H5File { ["Data"] = group }.Write(p);
                });
                Console.WriteLine($"  HDF5 (PureHDF):    {r.MedianMs,8:F2} ms");

                // ── Read 1 channel ──
                PrintSection("Read 1 channel");
                WriteMeasFlow(measPath, data);
                WriteHdf5(h5Path, data);

                r = Bench(() =>
                {
                    using var reader = MeasFile.OpenRead(measPath);
                    reader["Data"]["Signal"].ReadAll<float>();
                });
                Console.WriteLine($"  MeasFlow:          {r.MedianMs,8:F2} ms");

                r = Bench(() =>
                {
                    using var file = H5File.OpenRead(h5Path);
                    file.Dataset("/Data/Signal").Read<float[]>();
                });
                Console.WriteLine($"  HDF5 (PureHDF):    {r.MedianMs,8:F2} ms");

                // ── Read 10 channels ──
                PrintSection("Read 10 channels");
                {
                    var meas10Path = Path.Combine(tmpDir, "r10.meas");
                    using (var w10 = MeasFile.CreateWriter(meas10Path))
                    {
                        var g10 = w10.AddGroup("Data");
                        for (int c = 0; c < 10; c++)
                        {
                            var ch10 = g10.AddChannel<float>($"Ch{c}", trackStatistics: false);
                            ch10.Write(data.AsSpan());
                        }
                    }
                    r = Bench(() =>
                    {
                        using var reader = MeasFile.OpenRead(meas10Path);
                        for (int c = 0; c < 10; c++)
                            reader["Data"][$"Ch{c}"].ReadAll<float>();
                    });
                    Console.WriteLine($"  MeasFlow:          {r.MedianMs,8:F2} ms");

                    var h510Path = Path.Combine(tmpDir, "r10.h5");
                    {
                        var group10 = new H5Group();
                        for (int c = 0; c < 10; c++)
                            group10[$"Ch{c}"] = data;
                        new H5File { ["Data"] = group10 }.Write(h510Path);
                    }
                    r = Bench(() =>
                    {
                        using var file = H5File.OpenRead(h510Path);
                        for (int c = 0; c < 10; c++)
                            file.Dataset($"/Data/Ch{c}").Read<float[]>();
                    });
                    Console.WriteLine($"  HDF5 (PureHDF):    {r.MedianMs,8:F2} ms");
                }

                // ── Streaming write (MeasFlow only — HDF5 has no streaming support) ──
                PrintSection("Streaming write");
                r = Bench(() =>
                {
                    var p = Path.Combine(tmpDir, "stream.meas");
                    using var writer = MeasFile.CreateWriter(p);
                    var group = writer.AddGroup("Data");
                    var ch = group.AddChannel<float>("Signal", trackStatistics: false);
                    int chunk = n / 10;
                    for (int i = 0; i < 10; i++)
                    {
                        ch.Write(data.AsSpan(i * chunk, chunk));
                        writer.Flush();
                    }
                });
                Console.WriteLine($"  MeasFlow:          {r.MedianMs,8:F2} ms");

                // ── File size ──
                PrintSection("File size");
                WriteMeasFlow(measPath, data);
                Console.WriteLine($"  MeasFlow:          {new FileInfo(measPath).Length / 1024.0,8:F1} KB");

                WriteHdf5(h5Path, data);
                Console.WriteLine($"  HDF5 (PureHDF):    {new FileInfo(h5Path).Length / 1024.0,8:F1} KB");

                var rawPath = Path.Combine(tmpDir, "raw.bin");
                using (var fs = File.Create(rawPath))
                    fs.Write(MemoryMarshal.AsBytes(data.AsSpan()));
                Console.WriteLine($"  Raw binary:        {new FileInfo(rawPath).Length / 1024.0,8:F1} KB");
            }
            finally
            {
                Directory.Delete(tmpDir, true);
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static float[] GenerateData(int n)
    {
        var rng = new Random(42);
        var data = new float[n];
        for (int i = 0; i < n; i++)
            data[i] = (float)(rng.NextDouble() * 10000);
        return data;
    }

    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteMeasFlow(string path, float[] data)
    {
        using var writer = MeasFile.CreateWriter(path);
        var group = writer.AddGroup("Data");
        var ch = group.AddChannel<float>("Signal", trackStatistics: false);
        ch.Write(data.AsSpan());
    }

    private static void WriteHdf5(string path, float[] data)
    {
        new H5File
        {
            ["Data"] = new H5Group { ["Signal"] = data }
        }.Write(path);
    }

    private static void PrintSection(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"  {title}");
        Console.WriteLine(new string('-', 60));
    }

    private record BenchResult(double MedianMs, double MinMs, double MaxMs);

    private static BenchResult Bench(Action fn)
    {
        for (int i = 0; i < Warmup; i++)
            fn();

        var times = new double[Iterations];
        for (int i = 0; i < Iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            fn();
            sw.Stop();
            times[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(times);
        return new BenchResult(
            MedianMs: times[Iterations / 2],
            MinMs: times[0],
            MaxMs: times[Iterations - 1]
        );
    }
}
