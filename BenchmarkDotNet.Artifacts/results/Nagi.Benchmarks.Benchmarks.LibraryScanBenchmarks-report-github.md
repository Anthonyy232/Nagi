```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.7462)
Unknown processor
.NET SDK 10.0.101
  [Host]     : .NET 10.0.1 (10.0.125.57005), X64 RyuJIT AVX2
  DefaultJob : .NET 10.0.1 (10.0.125.57005), X64 RyuJIT AVX2


```
| Method          | SongCount | Mean      | Error    | StdDev    | Gen0      | Gen1      | Allocated |
|---------------- |---------- |----------:|---------:|----------:|----------:|----------:|----------:|
| **InitialScan**     | **100**       |  **55.36 ms** | **1.021 ms** |  **2.505 ms** |  **666.6667** |  **166.6667** |  **12.83 MB** |
| RescanNoChanges | 100       |  23.05 ms | 0.403 ms |  0.431 ms |   62.5000 |         - |   1.24 MB |
| **InitialScan**     | **500**       | **258.36 ms** | **4.833 ms** | **11.763 ms** | **4000.0000** | **1000.0000** |  **76.19 MB** |
| RescanNoChanges | 500       | 113.21 ms | 2.238 ms |  2.577 ms |  250.0000 |         - |   4.96 MB |
