using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Extracts raw PCM audio samples from audio files using FFmpeg.
/// </summary>
public class FFmpegPcmExtractor : IPcmExtractor
{
    private const int TargetSampleRate = 48000;
    private const int TargetChannels = 2;
    private const int BytesPerSample = 4; // F32LE
    private const int ReadBufferSize = 65536; // 64KB read buffer
    
    private readonly ILogger<FFmpegPcmExtractor> _logger;

    public FFmpegPcmExtractor(ILogger<FFmpegPcmExtractor> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private static ProcessStartInfo CreateFFmpegStartInfo(string filePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(filePath);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("f32le");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add(TargetChannels.ToString());
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add(TargetSampleRate.ToString());
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-sn");
        startInfo.ArgumentList.Add("-dn");
        startInfo.ArgumentList.Add("pipe:1");

        return startInfo;
    }

    private void KillProcessSafely(Process? process, string filePath)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _logger.LogDebug("Killed FFmpeg process for {FilePath}", filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to kill FFmpeg process for {FilePath}", filePath);
        }
    }

    /// <inheritdoc />
    public async Task<(float[] Samples, int SampleRate, int Channels)?> ExtractAsync(
        string filePath, 
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        var samplesList = new List<float[]>();
        
        await foreach (var chunk in ExtractStreamingAsync(filePath, cancellationToken))
        {
            // For full extract, we make a defensive copy because chunk.Samples might be pooled 
            // if we were using pooling in ExtractStreamingAsync (though presently we allocate new arrays for chunks)
            samplesList.Add(chunk.Samples);
        }

        if (samplesList.Count == 0) return null;

        var totalSamples = samplesList.Sum(s => s.Length);
        var result = new float[totalSamples];
        int offset = 0;
        foreach (var chunk in samplesList)
        {
            Array.Copy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }

        var duration = DateTime.UtcNow - startTime;
        _logger.LogInformation("Successfully extracted {SampleCount} samples from {FilePath} in {Duration}ms", 
            result.Length, filePath, (int)duration.TotalMilliseconds);

        return (result, TargetSampleRate, TargetChannels);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AudioChunk> ExtractStreamingAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting optimized streaming FFmpeg extraction: {FilePath}", filePath);

        Process? process = null;
        try
        {
            process = Process.Start(CreateFFmpegStartInfo(filePath));
            if (process == null)
            {
                _logger.LogError("Failed to start FFmpeg process");
                yield break;
            }

            // Zero-copy strategy:
            // 1. Read raw bytes into a pooled buffer
            // 2. Combine with previous leftovers
            // 3. Cast the span of bytes directly to floats using MemoryMarshal
            byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
            byte[] leftovers = new byte[BytesPerSample];
            int leftoverCount = 0;
            
            try
            {
                using var stream = process.StandardOutput.BaseStream;
                int bytesRead;
                
                while ((bytesRead = await stream.ReadAsync(buffer, 0, ReadBufferSize, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    int totalAvailable = bytesRead + leftoverCount;
                    int floatCount = totalAvailable / BytesPerSample;
                    
                    if (floatCount > 0)
                    {
                        // Use a fresh array for the chunk as it will be consumed asynchronously
                        float[] floatArray = new float[floatCount];
                        var byteSpan = floatArray.AsSpan().AsBytes();
                        
                        int bytesToCopyFromBuffer = bytesRead;
                        int bytesCopiedFromLeftovers = 0;
                        
                        if (leftoverCount > 0)
                        {
                            leftovers.AsSpan(0, leftoverCount).CopyTo(byteSpan);
                            bytesCopiedFromLeftovers = leftoverCount;
                        }
                        
                        int remainingSpaceInResult = byteSpan.Length - bytesCopiedFromLeftovers;
                        int bytesActuallyConsumedFromBuffer = Math.Min(bytesRead, remainingSpaceInResult);
                        
                        buffer.AsSpan(0, bytesActuallyConsumedFromBuffer)
                              .CopyTo(byteSpan.Slice(bytesCopiedFromLeftovers));
                        
                        yield return new AudioChunk(floatArray, TargetSampleRate, TargetChannels);
                        
                        // Update leftovers
                        leftoverCount = bytesRead - bytesActuallyConsumedFromBuffer;
                        if (leftoverCount > 0)
                        {
                            buffer.AsSpan(bytesActuallyConsumedFromBuffer, leftoverCount)
                                  .CopyTo(leftovers);
                        }
                    }
                    else
                    {
                        // Update leftovers
                        buffer.AsSpan(0, bytesRead).CopyTo(leftovers.AsSpan(leftoverCount));
                        leftoverCount += bytesRead;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("FFmpeg exited with error code {ExitCode}: {Error}", process.ExitCode, error);
            }
        }
        finally
        {
            KillProcessSafely(process, filePath);
            process?.Dispose();
        }
    }
}

internal static class SpanExtensions
{
    public static Span<byte> AsBytes(this Span<float> floatSpan) 
        => MemoryMarshal.AsBytes(floatSpan);
}
