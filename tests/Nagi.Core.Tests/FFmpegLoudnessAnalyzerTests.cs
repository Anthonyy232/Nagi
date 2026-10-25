using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Services.Implementations;
using Xunit;

namespace Nagi.Core.Tests;

public sealed class FFmpegLoudnessAnalyzerTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("Nagi-Loudness-");
    private readonly FFmpegLoudnessAnalyzer _analyzer = new(NullLogger<FFmpegLoudnessAnalyzer>.Instance);

    // 997 Hz rather than 1 kHz: FFmpeg 9 probes a 48 kHz float WAV of an exact 1 kHz sine as MPEG-TS.
    [Theory]
    [InlineData(44100, 997,0.125892541, -18)]
    [InlineData(48000, 997, 0.125892541, -18)]
    [InlineData(96000, 997, 0.125892541, -18)]
    [InlineData(48000, 440, 1, -0.65)]
    public async Task StereoReferenceTones_MeasureExpectedLoudnessAndPeak(
        int sampleRate, int frequency, double amplitude, double expectedLufs)
    {
        var signal = FormattableString.Invariant($"{amplitude}*sin(2*PI*{frequency}*t)");
        var path = await GenerateAsync($"aevalsrc={signal}|{signal}:s={sampleRate}:d=3");

        var result = await _analyzer.AnalyzeAsync(path);

        result.Should().NotBeNull();
        result!.Value.LoudnessLufs.Should().BeApproximately(expectedLufs, 0.1);
        result.Value.Peak.Should().BeApproximately(amplitude, 0.001);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("0.000001*sin(2*PI*1000*t)", 1)]
    [InlineData("sin(2*PI*1000*t)", 0.2)]
    public async Task SilentOrTooShortAudio_HasNoLoudness(string signal, double duration)
    {
        var path = await GenerateAsync(FormattableString.Invariant($"aevalsrc={signal}|{signal}:s=48000:d={duration}"));

        (await _analyzer.AnalyzeAsync(path)).Should().BeNull();
    }

    [Fact]
    public async Task FinalPartialBlock_ContributesToPeak()
    {
        var signal = "if(lt(t\\,0.4)\\,0.1\\,0.9)*sin(2*PI*1000*t)";
        var path = await GenerateAsync($"aevalsrc={signal}|{signal}:s=48000:d=0.45");
        var result = await _analyzer.AnalyzeAsync(path);

        result.Should().NotBeNull();
        result!.Value.Peak.Should().BeApproximately(0.9, 0.000001);
    }

    [Fact]
    public async Task QuietSections_AreExcludedByTheRelativeLoudnessGate()
    {
        var signal = "if(between(t\\,3\\,6)\\,0.125892541\\,0.00125892541)*sin(2*PI*1000*t)";
        var path = await GenerateAsync($"aevalsrc={signal}|{signal}:s=48000:d=9");

        var result = await _analyzer.AnalyzeAsync(path);

        result.Should().NotBeNull();
        // Transition windows contribute 30 full-block equivalents across 33 gated blocks.
        var expectedLufs = -18 + 10 * Math.Log10(30d / 33);
        result!.Value.LoudnessLufs.Should().BeApproximately(expectedLufs, 0.1);
    }

    [Fact]
    public async Task CorruptAudio_WithLargeErrorOutput_ReportsFailureWithoutStalling()
    {
        var path = Path.Combine(_directory.FullName, "corrupt.aac");
        await RunFFmpegAsync("-f", "lavfi", "-i", "sine=sample_rate=48000",
            "-t", "8", "-c:a", "aac", "-bsf:a", "noise=amount=4", path);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var analyze = () => _analyzer.AnalyzeAsync(path, timeout.Token);

        await analyze.Should().ThrowAsync<InvalidDataException>();
        timeout.IsCancellationRequested.Should().BeFalse();
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Cancellation_StopsDecoderAndReleasesInputFile()
    {
        var path = await GenerateAsync("anullsrc=r=48000:cl=stereo:d=60", "flac");
        using var cancellation = new CancellationTokenSource();
        var analyzing = _analyzer.AnalyzeAsync(path, cancellation.Token);
        cancellation.Cancel();

        var wait = async () => await analyzing;
        await wait.Should().ThrowAsync<OperationCanceledException>();
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var valid = await GenerateAsync("aevalsrc=0.125892541*sin(2*PI*997*t)|0.125892541*sin(2*PI*997*t):s=48000:d=3");
        (await _analyzer.AnalyzeAsync(valid)).Should().NotBeNull();
    }

    [Fact]
    public async Task ConcurrentAnalyses_ReturnEachTracksLoudnessAndPeak()
    {
        var quietPath = Path.Combine(_directory.FullName, "quiet.wav");
        var loudPath = Path.Combine(_directory.FullName, "loud.wav");
        await RunFFmpegAsync("-f", "lavfi", "-i", "aevalsrc=0.125892541*sin(2*PI*997*t)|0.125892541*sin(2*PI*997*t):s=48000:d=3", "-c:a", "pcm_f32le", quietPath);
        await RunFFmpegAsync("-f", "lavfi", "-i", "aevalsrc=0.5*sin(2*PI*440*t)|0.5*sin(2*PI*440*t):s=48000:d=3", "-c:a", "pcm_f32le", loudPath);

        var quietTask = _analyzer.AnalyzeAsync(quietPath);
        var loudTask = _analyzer.AnalyzeAsync(loudPath);
        await Task.WhenAll(quietTask, loudTask);

        quietTask.Result.Should().NotBeNull();
        loudTask.Result.Should().NotBeNull();
        quietTask.Result!.Value.Peak.Should().BeApproximately(0.125892541, 0.001);
        loudTask.Result!.Value.Peak.Should().BeApproximately(0.5, 0.001);
        loudTask.Result.Value.LoudnessLufs.Should().BeGreaterThan(quietTask.Result.Value.LoudnessLufs + 10);
    }

    private async Task<string> GenerateAsync(string source, string codec = "pcm_f32le")
    {
        var path = Path.Combine(_directory.FullName, codec == "flac" ? "audio sample.flac" : "audio sample.wav");
        await RunFFmpegAsync("-f", "lavfi", "-i", source, "-c:a", codec, path);
        return path;
    }

    private static async Task RunFFmpegAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            // Only catches a hung ffmpeg: CI runners can stall a launch for many seconds.
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            process.ExitCode.Should().Be(0, await errors);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
