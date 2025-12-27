using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

public class FFmpegService : IFFmpegService
{
    private readonly ILogger<FFmpegService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool? _isInstalled;

    public FFmpegService(ILogger<FFmpegService> logger)
    {
        _logger = logger;
    }

    public async Task<bool> IsFFmpegInstalledAsync(bool forceRecheck = false)
    {
        if (!forceRecheck && _isInstalled.HasValue)
        {
            return _isInstalled.Value;
        }

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Clear cache if forcing recheck
            if (forceRecheck)
            {
                _isInstalled = null;
            }
            else if (_isInstalled.HasValue)
            {
                return _isInstalled.Value;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-version");

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _isInstalled = false;
                return false;
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            _isInstalled = process.ExitCode == 0;
            return _isInstalled.Value;
        }
        catch
        {
            _isInstalled = false;
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public string GetFFmpegSetupInstructions()
    {
        return "FFmpeg is required for High-Precision Volume Normalization (ReplayGain analysis) but was not found on your system.\n\n" +
               "Follow these steps to set it up:\n\n" +
               "1. **Download**: Visit https://www.gyan.dev/ffmpeg/builds/ and download the 'ffmpeg-git-full.7z' (or similar) essentials build.\n" +
               "2. **Guide**: You can follow this guide for detailed instructions: https://phoenixnap.com/kb/ffmpeg-windows\n" +
               "3. **Extract**: Open the architecture folder and locate the 'bin' directory containing `ffmpeg.exe`.\n" +
               "4. **Install**: Copy `ffmpeg.exe` to a permanent folder (e.g., `C:\\ffmpeg\\`) and **add that folder to your System PATH** environment variable.\n" +
               "5. **Restart**: Please restart Nagi after installation for changes to take effect.";
    }
}
