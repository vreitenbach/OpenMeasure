using BenchmarkDotNet.Running;
using MeasFlow.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(WriteBenchmarks).Assembly).Run(args);
