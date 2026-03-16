using BenchmarkDotNet.Running;
using MeasFlow.Benchmarks;

if (args.Contains("--ci"))
{
    CiRunner.Run();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(WriteBenchmarks).Assembly).Run(args);
