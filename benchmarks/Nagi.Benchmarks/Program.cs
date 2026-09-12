using BenchmarkDotNet.Running;

namespace Nagi.Benchmarks;

public class Program
{
    public static int Main(string[] args)
    {
        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args).ToArray();
        return summaries.Length == 0 || summaries.Any(summary =>
            summary.HasCriticalValidationErrors || summary.Reports.Length == 0 ||
            summary.Reports.Any(report => !report.Success || report.ResultStatistics is null)) ? 1 : 0;
    }
}
