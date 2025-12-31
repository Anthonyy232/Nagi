using BenchmarkDotNet.Running;
using Nagi.Benchmarks.Benchmarks;

namespace Nagi.Benchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        BenchmarkRunner.Run<LibraryScanBenchmarks>();
    }
}
