using BenchmarkDotNet.Running;
using Quickshell.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(ByteScanBenchmarks).Assembly).Run(args);
