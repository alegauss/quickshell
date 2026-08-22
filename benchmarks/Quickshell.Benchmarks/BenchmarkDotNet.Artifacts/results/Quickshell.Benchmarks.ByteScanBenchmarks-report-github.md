```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
Intel Core i7-14700 2.10GHz, 1 CPU, 28 logical and 20 physical cores
.NET SDK 10.0.303
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  Job-UJZYQN : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

IterationCount=4  LaunchCount=1  WarmupCount=2  

```
| Method       | Stream      | Mean          | Error         | StdDev     | Allocated |
|------------- |------------ |--------------:|--------------:|-----------:|----------:|
| **CountEscapes** | **cat-log**     | **10,667.890 μs** | **1,259.9217 μs** | **69.0606 μs** |         **-** |
| **CountEscapes** | **dmesg**       |     **93.976 μs** |    **49.0541 μs** |  **7.5912 μs** |         **-** |
| **CountEscapes** | **htop**        |      **9.414 μs** |     **2.3238 μs** |  **0.3596 μs** |         **-** |
| **CountEscapes** | **ls-color-r**  |    **338.618 μs** |    **11.8916 μs** |  **1.8402 μs** |         **-** |
| **CountEscapes** | **tmux-resize** |     **10.268 μs** |     **0.6641 μs** |  **0.1028 μs** |         **-** |
| **CountEscapes** | **vim-scroll**  |    **160.143 μs** |    **20.2443 μs** |  **1.1097 μs** |         **-** |
