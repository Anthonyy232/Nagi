using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

public sealed class FFmpegLoudnessAnalyzer(ILogger<FFmpegLoudnessAnalyzer> logger) : ILoudnessAnalyzer
{
    public async Task<(double LoudnessLufs, double Peak)?> AnalyzeAsync(string filePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        string[] arguments =
        [
            "-v", "error", "-nostdin", "-i", filePath, "-map", "0:a:0",
            "-af", "aformat=sample_fmts=flt:sample_rates=48000:channel_layouts=stereo," +
                   "ebur128=metadata=1," +
                   "astats=metadata=1:measure_perchannel=none:measure_overall=Peak_level," +
                   "ametadata=print:file=-",
            "-f", "null", "-"
        ];
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;
        try
        {
            // Drain stderr concurrently so decoder errors cannot block the metadata pipe.
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null) logger.LogDebug("FFmpeg analyzing {FilePath}: {Error}", filePath, args.Data);
            };
            process.BeginErrorReadLine();

            const string loudnessKey = "lavfi.r128.I=";
            const string peakKey = "lavfi.astats.Overall.Peak_level=";
            double? loudness = null;
            double? peakDb = null;
            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith(loudnessKey, StringComparison.Ordinal))
                    loudness = double.Parse(line.AsSpan(loudnessKey.Length), CultureInfo.InvariantCulture);
                else if (line.StartsWith(peakKey, StringComparison.Ordinal))
                    peakDb = line.AsSpan(peakKey.Length).SequenceEqual("-inf")
                        ? double.NegativeInfinity
                        : double.Parse(line.AsSpan(peakKey.Length), CultureInfo.InvariantCulture);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidDataException($"FFmpeg could not analyze '{filePath}' (exit code {process.ExitCode}).");

            // ebur128 reports -70 until a complete block exceeds its absolute gate.
            return loudness is > -70 && peakDb is { } peak && double.IsFinite(peak)
                ? (loudness.Value, Math.Pow(10, peak / 20))
                : null;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }
}
