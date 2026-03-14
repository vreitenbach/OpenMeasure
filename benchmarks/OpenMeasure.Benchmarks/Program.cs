using BenchmarkDotNet.Running;
using OpenMeasure.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(WriteBenchmarks).Assembly).Run(args);
