using Nagi.Services.Abstractions;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

#if !MSIX_PACKAGE
using Velopack;
#endif

namespace Nagi.Services.Implementations;

#if MSIX_PACKAGE
/// <summary>
/// A dummy implementation of <see cref="IUpdateService"/> for MSIX builds.
/// Updates for MSIX packages are handled by the Microsoft Store, so this service does nothing.
/// </summary>
public class VelopackUpdateService : IUpdateService
{
    public Task CheckForUpdatesOnStartupAsync()
    {
        Debug.WriteLine("[INFO] VelopackUpdateService: Skipping update check in MSIX packaged mode.");
        return Task.CompletedTask;
    }

    public Task CheckForUpdatesManuallyAsync()
    {
        Debug.WriteLine("[INFO] VelopackUpdateService: Manual update check is not available for MSIX packages.");
        return Task.CompletedTask;
    }
}
#else
/// <summary>
/// An implementation of <see cref="IUpdateService"/> that uses the Velopack framework for application updates.
/// </summary>
public class VelopackUpdateService : IUpdateService {
    private readonly UpdateManager _updateManager;
    private readonly ISettingsService _settingsService;
    private readonly IUIService _uiService;

    public VelopackUpdateService(
        UpdateManager updateManager,
        ISettingsService settingsService,
        IUIService uiService) {
        _updateManager = updateManager ?? throw new ArgumentNullException(nameof(updateManager));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));
    }

    /// <summary>
    /// Checks for updates upon application startup. This check is skipped in DEBUG builds or if disabled by the user.
    /// If an update is found and has not been previously skipped, it prompts the user for action.
    /// </summary>
    public async Task CheckForUpdatesOnStartupAsync() {
#if DEBUG
        Debug.WriteLine("[INFO] VelopackUpdateService: Skipping update check in DEBUG mode.");
        return;
#endif

        if (!await _settingsService.GetCheckForUpdatesEnabledAsync()) {
            Debug.WriteLine("[INFO] VelopackUpdateService: Automatic update check is disabled by user setting.");
            return;
        }

        try {
            UpdateInfo? updateInfo = await _updateManager.CheckForUpdatesAsync();
            if (updateInfo == null) {
                Debug.WriteLine("[INFO] VelopackUpdateService: No updates found on startup.");
                return;
            }

            string? lastSkippedVersion = await _settingsService.GetLastSkippedUpdateVersionAsync();
            if (lastSkippedVersion == updateInfo.TargetFullRelease.Version.ToString()) {
                Debug.WriteLine($"[INFO] VelopackUpdateService: User has previously skipped version {lastSkippedVersion}.");
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
            Debug.WriteLine($"[ERROR] VelopackUpdateService: Failed while checking for updates on startup. {ex.Message}");
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
            Debug.WriteLine($"[ERROR] VelopackUpdateService: Failed during manual update check. {ex.Message}");
            await _uiService.ShowMessageDialogAsync("Update Error", $"An error occurred while checking for updates: {ex.Message}");
        }
    }

    private async Task DownloadAndApplyUpdateAsync(UpdateInfo updateInfo) {
        try {
            // Inform the user that the download is starting and the app will restart.
            await _uiService.ShowMessageDialogAsync("Downloading Update", $"Version {updateInfo.TargetFullRelease.Version} is being downloaded. The application will restart automatically when it's ready.");
            await _updateManager.DownloadUpdatesAsync(updateInfo);
            _updateManager.ApplyUpdatesAndRestart(updateInfo);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[ERROR] VelopackUpdateService: Failed to download or apply update. {ex.Message}");
            await _uiService.ShowMessageDialogAsync("Update Error", $"An error occurred while installing the update: {ex.Message}");
        }
    }
}
#endif