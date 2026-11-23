using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Nagi.Core.Playback;

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
            "-f", Ebur128Meter.FFmpegSampleFormat, "-ac", Ebur128Meter.Channels.ToString(CultureInfo.InvariantCulture),
            "-ar", Ebur128Meter.SampleRate.ToString(CultureInfo.InvariantCulture), "-vn", "-sn", "-dn", "pipe:1"
        ];
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var meter = new Ebur128Meter();
        using var process = Process.Start(startInfo)!;
        try
        {
            // Drain stderr concurrently so decoder errors cannot block the audio pipe.
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null) logger.LogDebug("FFmpeg analyzing {FilePath}: {Error}", filePath, args.Data);
            };
            process.BeginErrorReadLine();

            var buffer = new byte[65536];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAtLeastAsync(buffer, buffer.Length,
                       throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false)) != 0)
            {
                meter.AddFrames(buffer.AsSpan(0, count));
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidDataException($"FFmpeg could not analyze '{filePath}' (exit code {process.ExitCode}).");

            return meter.GetResult();
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            // the token may already be cancelled; the killed process still has to be reaped
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
