using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.ViewModels;

/// <summary>
///     Settings section for playlist import/export, backup and restore, and resetting the app.
/// </summary>
public partial class DataManagementSettingsViewModel : ObservableObject
{
    private readonly IUIService _uiService;
    private readonly IPlaylistExportService _playlistExportService;
    private readonly IBackupRestoreService _backupRestoreService;
    private readonly IApplicationLifecycle _applicationLifecycle;
    private readonly ILogger<DataManagementSettingsViewModel> _logger;

    public DataManagementSettingsViewModel(
        IUIService uiService,
        IPlaylistExportService playlistExportService,
        IBackupRestoreService backupRestoreService,
        IApplicationLifecycle applicationLifecycle,
        ILogger<DataManagementSettingsViewModel> logger)
    {
        _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));
        _playlistExportService = playlistExportService ?? throw new ArgumentNullException(nameof(playlistExportService));
        _backupRestoreService = backupRestoreService ?? throw new ArgumentNullException(nameof(backupRestoreService));
        _applicationLifecycle = applicationLifecycle ?? throw new ArgumentNullException(nameof(applicationLifecycle));
        _logger = logger;
    }

    [RelayCommand]
    private async Task ResetApplicationDataAsync()
    {
        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            Strings.Settings_Reset_Title,
            Strings.Settings_Reset_Message,
            Strings.Settings_Reset_Button,
            null);

        if (!confirmed) return;

        try
        {
            await _applicationLifecycle.ResetAndRestartAsync();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Application reset failed");
            await _uiService.ShowMessageDialogAsync(
                Strings.Settings_ResetError_Title,
                string.Format(Strings.Settings_ResetError_Message, ex.Message));
        }
    }

    [RelayCommand]
    private async Task ExportAllPlaylistsAsync()
    {
        var folderPath = await _uiService.PickSingleFolderAsync();
        if (folderPath is null) return;

        var result = await _playlistExportService.ExportAllPlaylistsAsync(folderPath);

        if (result.Success)
        {
            await _uiService.ShowMessageDialogAsync(Strings.Settings_Export_Success_Title,
                ResourceFormatter.Format(Strings.Settings_Export_Success_Message, result.PlaylistsExported, result.TotalSongs, folderPath));
        }
        else
        {
            await _uiService.ShowMessageDialogAsync(Strings.Settings_Export_Failed_Title, result.ErrorMessage ?? Strings.Settings_Export_Failed_Message);
        }
    }

    [RelayCommand]
    private async Task ImportMultiplePlaylistsAsync()
    {
        var filePaths = await _uiService.PickOpenMultipleFilesAsync([".m3u", ".m3u8"]);
        if (filePaths.Count == 0) return;

        var result = await _playlistExportService.ImportMultiplePlaylistsAsync(filePaths);

        if (result.Success)
        {
            var message = ResourceFormatter.Format(Strings.Settings_Import_Success_Message, result.PlaylistsImported, result.TotalMatchedSongs);
            if (result.TotalUnmatchedSongs > 0)
            {
                message += ResourceFormatter.Format(Strings.Settings_Import_Unmatched_Message, result.TotalUnmatchedSongs);
            }
            if (result.FailedFiles.Count > 0)
            {
                message += ResourceFormatter.Format(Strings.Settings_Import_FailedFiles_Message, result.FailedFiles.Count);
            }
            await _uiService.ShowMessageDialogAsync(Strings.Settings_Import_Success_Title, message);
        }
        else
        {
            await _uiService.ShowMessageDialogAsync(Strings.Settings_Import_FailedTotal_Title, Strings.Settings_Import_FailedTotal_Message);
        }
    }

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        var folderPath = await _uiService.PickSingleFolderAsync();
        if (folderPath is null) return;

        var result = await _backupRestoreService.CreateBackupAsync(folderPath);

        if (result.Success)
        {
            await _uiService.ShowMessageDialogAsync(
                Strings.Settings_Backup_Success_Title,
                string.Format(Strings.Settings_Backup_Success_Message, result.BackupSizeMB, result.BackupFilePath));
        }
        else
        {
            await _uiService.ShowMessageDialogAsync(
                Strings.Settings_Backup_Failed_Title,
                result.ErrorMessage ?? Strings.Settings_Backup_Failed_Message);
        }
    }

    [RelayCommand]
    private async Task RestoreFromBackupAsync()
    {
        var backupFilePath = await _uiService.PickSingleFileAsync(new[] { ".zip" });
        if (string.IsNullOrEmpty(backupFilePath)) return;

        var isValid = await _backupRestoreService.ValidateBackupAsync(backupFilePath);
        if (!isValid)
        {
            await _uiService.ShowMessageDialogAsync(
                Strings.Settings_Restore_Invalid_Title,
                Strings.Settings_Restore_Invalid_Message);
            return;
        }

        var confirmed = await _uiService.ShowConfirmationDialogAsync(
            Strings.Settings_Restore_Confirm_Title,
            Strings.Settings_Restore_Confirm_Message,
            Strings.Settings_Restore_Confirm_Button,
            Strings.Generic_Cancel);

        if (!confirmed) return;

        var result = await _backupRestoreService.RestoreFromBackupAsync(backupFilePath);

        if (result.Success)
        {
            await _uiService.ShowMessageDialogAsync(
                Strings.Settings_Restore_Success_Title,
                Strings.Settings_Restore_Success_Message);

            if (result.RequiresRestart)
            {
                _applicationLifecycle.Restart();
            }
        }
        else
        {
            await _uiService.ShowMessageDialogAsync(
                Strings.Settings_Restore_Failed_Title,
                result.ErrorMessage ?? Strings.Settings_Restore_Failed_Message);
        }
    }
}
