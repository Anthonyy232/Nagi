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
        return Resources.Strings.FFmpeg_SetupInstructions;
    }
}
