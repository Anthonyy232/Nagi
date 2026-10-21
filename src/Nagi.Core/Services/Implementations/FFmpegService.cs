using System.Diagnostics;
using System.Threading.Tasks;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

public class FFmpegService : IFFmpegService
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool? _isInstalled;

    public async Task<bool> IsFFmpegInstalledAsync(bool forceRecheck = false)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!forceRecheck && _isInstalled.HasValue)
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

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
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
}
