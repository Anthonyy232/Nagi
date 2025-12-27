using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Extracts raw PCM audio samples from audio files using FFmpeg.
///     More robust and efficient than LibVLC for this specific task.
/// </summary>
public class FFmpegPcmExtractor : IPcmExtractor
{
    private const int TargetSampleRate = 48000;
    private const int TargetChannels = 2;
    private const int BytesPerSample = 4; // F32
    
    private readonly ILogger<FFmpegPcmExtractor> _logger;

    public FFmpegPcmExtractor(ILogger<FFmpegPcmExtractor> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<(float[] Samples, int SampleRate, int Channels)?> ExtractAsync(
        string filePath, 
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogDebug("Starting FFmpeg PCM extraction: {FilePath}", filePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Use ArgumentList for safer path handling (handles spaces/quotes correctly)
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
        startInfo.ArgumentList.Add("-vn"); // No video
        startInfo.ArgumentList.Add("-sn"); // No subtitles
        startInfo.ArgumentList.Add("-dn"); // No data
        startInfo.ArgumentList.Add("pipe:1");

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogError("Failed to start FFmpeg process");
                return null;
            }

            var samplesList = new List<float[]>();
            var buffer = new byte[65536];
            var leftover = new byte[BytesPerSample];
            int leftoverCount = 0;
            
            using var stream = process.StandardOutput.BaseStream;
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                int totalAvailable = bytesRead + leftoverCount;
                int floatCount = totalAvailable / BytesPerSample;
                
                if (floatCount > 0)
                {
                    var floatArray = new float[floatCount];
                    int floatsFilled = 0;

                    // Handle leftovers from previous read
                    int bufferOffset = 0;
                    if (leftoverCount > 0)
                    {
                        int bytesNeeded = BytesPerSample - leftoverCount;
                        if (bytesRead >= bytesNeeded)
                        {
                            var temp = new byte[BytesPerSample];
                            Buffer.BlockCopy(leftover, 0, temp, 0, leftoverCount);
                            Buffer.BlockCopy(buffer, 0, temp, leftoverCount, bytesNeeded);
                            floatArray[0] = BitConverter.ToSingle(temp, 0);
                            floatsFilled = 1;
                            bufferOffset = bytesNeeded;
                        }
                    }

                    // Process bulk from current buffer
                    int remainingBytesInBuf = bytesRead - bufferOffset;
                    int floatsInBuf = remainingBytesInBuf / BytesPerSample;
                    if (floatsInBuf > 0)
                    {
                        Buffer.BlockCopy(buffer, bufferOffset, floatArray, floatsFilled, floatsInBuf * BytesPerSample);
                        floatsFilled += floatsInBuf;
                        bufferOffset += floatsInBuf * BytesPerSample;
                    }

                    samplesList.Add(floatArray);
                    
                    // Update leftovers
                    leftoverCount = bytesRead - bufferOffset;
                    if (leftoverCount > 0)
                    {
                        Buffer.BlockCopy(buffer, bufferOffset, leftover, 0, leftoverCount);
                    }
                }
                else
                {
                    // Not enough bytes for even one float, add all to leftovers
                    Buffer.BlockCopy(buffer, 0, leftover, leftoverCount, bytesRead);
                    leftoverCount += bytesRead;
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("FFmpeg exited with error code {ExitCode}: {Error}", process.ExitCode, error);
                
                // If we got some samples, we might still want to return them if it's just a non-fatal error at the end
                if (samplesList.Count == 0) return null;
            }

            var totalSamples = samplesList.Sum(s => s.Length);
            if (totalSamples == 0)
            {
                _logger.LogWarning("No samples extracted from: {FilePath}", filePath);
                return null;
            }

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
        catch (OperationCanceledException)
        {
            _logger.LogDebug("FFmpeg extraction cancelled for {FilePath}", filePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting PCM with FFmpeg from {FilePath}", filePath);
            return null;
        }
    }
}
