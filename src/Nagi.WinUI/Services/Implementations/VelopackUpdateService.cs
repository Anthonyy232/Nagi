using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Services.Abstractions;

#if !MSIX_PACKAGE
using Velopack;
#endif

namespace Nagi.WinUI.Services.Implementations;

#if MSIX_PACKAGE
/// <summary>
///     A dummy implementation of <see cref="IUpdateService" /> for MSIX builds.
///     Updates for MSIX packages are handled by the Microsoft Store, so this service does nothing.
/// </summary>
public class VelopackUpdateService : IUpdateService
{
    private readonly ILogger<VelopackUpdateService> _logger;

    public VelopackUpdateService(ILogger<VelopackUpdateService> logger)
    {
        _logger = logger;
    }

    public Task CheckForUpdatesOnStartupAsync()
    {
        _logger.LogInformation("Skipping update check in MSIX packaged mode.");
        return Task.CompletedTask;
    }

    public Task CheckForUpdatesManuallyAsync()
    {
        _logger.LogInformation("Manual update check is not available for MSIX packages.");
        return Task.CompletedTask;
    }
}
#else
/// <summary>
/// An implementation of <see cref="IUpdateService"/> that uses the Velopack framework for application updates.
/// </summary>
public class VelopackUpdateService : IUpdateService {
    private readonly UpdateManager _updateManager;
    private readonly IUISettingsService _settingsService;
    private readonly IUIService _uiService;
    private readonly ILogger<VelopackUpdateService> _logger;

    public VelopackUpdateService(
        UpdateManager updateManager,
        IUISettingsService settingsService,
        IUIService uiService,
        ILogger<VelopackUpdateService> logger) {
        _updateManager = updateManager ?? throw new ArgumentNullException(nameof(updateManager));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Checks for updates upon application startup. This check is skipped in DEBUG builds or if disabled by the user.
    /// If an update is found and has not been previously skipped, it prompts the user for action.
    /// </summary>
    public async Task CheckForUpdatesOnStartupAsync() {
#if DEBUG
        _logger.LogDebug("Skipping update check in DEBUG mode.");
        return;
#endif

        if (!await _settingsService.GetCheckForUpdatesEnabledAsync()) {
            _logger.LogInformation("Automatic update check is disabled by user setting.");
            return;
        }

        try {
            UpdateInfo? updateInfo = await _updateManager.CheckForUpdatesAsync();
            if (updateInfo == null) {
                _logger.LogInformation("No updates found on startup.");
                return;
            }

            string? lastSkippedVersion = await _settingsService.GetLastSkippedUpdateVersionAsync();
            if (lastSkippedVersion == updateInfo.TargetFullRelease.Version.ToString()) {
                _logger.LogInformation("User has previously skipped version {SkippedVersion}.", lastSkippedVersion);
                return;
            }

            UpdateDialogResult result = await _uiService.ShowUpdateDialogAsync(
                "Update Available",
                $"A new version ({updateInfo.TargetFullRelease.Version}) is available. Would you like to download and install it now?",
                "Install Now",
                "Later",
                "Skip This Version");

            switch (result) {
                case UpdateDialogResult.Install:
                    await DownloadAndApplyUpdateAsync(updateInfo);
                    break;
                case UpdateDialogResult.Skip:
                    await _settingsService.SetLastSkippedUpdateVersionAsync(updateInfo.TargetFullRelease.Version.ToString());
                    break;
                case UpdateDialogResult.RemindLater:
                default:
                    // Do nothing; the user will be prompted on the next startup.
                    break;
            }
        }
        catch (Exception ex) {
            // This is a background task, so we log the error without disturbing the user with a dialog.
            _logger.LogError(ex, "Failed while checking for updates on startup.");
        }
    }

    /// <summary>
    /// Manually triggers an update check. This method provides UI feedback to the user regarding the outcome,
    /// whether an update is available, if the application is up-to-date, or if an error occurred.
    /// </summary>
    public async Task CheckForUpdatesManuallyAsync() {
#if DEBUG
        await _uiService.ShowMessageDialogAsync("Debug Mode", "Update checks are disabled in debug mode. This dialog indicates the function was called.");
        return;
#endif

        try {
            UpdateInfo? updateInfo = await _updateManager.CheckForUpdatesAsync();

            if (updateInfo == null) {
                await _uiService.ShowMessageDialogAsync("Up to Date", "You are running the latest version of Nagi.");
                return;
            }

            bool confirmed = await _uiService.ShowConfirmationDialogAsync(
                "Update Available",
                $"A new version ({updateInfo.TargetFullRelease.Version}) is available. Would you like to download and install it now?",
                "Install Now",
                "Cancel");

            if (confirmed) {
                await DownloadAndApplyUpdateAsync(updateInfo);
            }
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed during manual update check.");
            await _uiService.ShowMessageDialogAsync("Update Error", $"An error occurred while checking for updates: {ex.Message}");
        }
    }

    // Handles the process of downloading and applying an update, providing user feedback.
    private async Task DownloadAndApplyUpdateAsync(UpdateInfo updateInfo) {
        try {
            await _uiService.ShowMessageDialogAsync("Downloading Update", $"Version {updateInfo.TargetFullRelease.Version} is being downloaded. The application will restart automatically when it's ready.");
            await _updateManager.DownloadUpdatesAsync(updateInfo);
            _updateManager.ApplyUpdatesAndRestart(updateInfo);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to download or apply update.");
            await _uiService.ShowMessageDialogAsync("Update Error", $"An error occurred while installing the update: {ex.Message}");
        }
    }
}
#endif