using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Core.Models;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services.Implementations;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Dialogs;
using Nagi.WinUI.Helpers;
using WinRT.Interop;

namespace Nagi.WinUI.Services.Implementations;

public partial class UIService : IUIService
{
    private readonly ViewModelFactory _viewModelFactory;
    private readonly ILogger<UIService> _logger;
    private readonly MainWindow _window;

    public UIService(MainWindow window, ViewModelFactory viewModelFactory,
        ILogger<UIService> logger)
    {
        _window = window;
        _viewModelFactory = viewModelFactory;
        _logger = logger;
    }

    public async Task<bool> ShowConfirmationDialogAsync(string title, string content, string? primaryButtonText,
        string? closeButtonText, bool defaultToCancel)
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return false;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText ?? Strings.Generic_OK,
            CloseButtonText = closeButtonText ?? Strings.Generic_Cancel,
            XamlRoot = xamlRoot,
            DefaultButton = defaultToCancel ? ContentDialogButton.Close : ContentDialogButton.Primary
        };

        var result = await ShowDialogAsync(dialog);
        return result == ContentDialogResult.Primary;
    }

    public async Task<string?> PickSingleFolderAsync()
    {
        try
        {
            var selectedFolder = await new FolderPicker(_window.AppWindow.Id).PickSingleFolderAsync();
            return selectedFolder?.Path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show the folder picker");
            return null;
        }
    }

    public async Task ShowInFolderAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        var folderPath = Path.GetDirectoryName(filePath);
        if (folderPath is null) return;

        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            await Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open folder {FolderPath}", folderPath);
        }
    }

    public async Task<bool> OpenUriAsync(Uri uri)
    {
        try
        {
            return await Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open {Uri}", uri);
            return false;
        }
    }

    public async Task ShowMessageDialogAsync(string title, string message)
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = Strings.Generic_OK,
            XamlRoot = xamlRoot
        };

        await ShowDialogAsync(dialog);
    }

    /// <summary>
    ///     Tells the user the app crashed, shows <paramref name="report" /> for an issue report, and offers a data reset.
    /// </summary>
    /// <returns>Whether the user asked for the reset.</returns>
    public async Task<bool> ShowCrashReportDialogAsync(string report)
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return false;

        var dialog = new ContentDialog
        {
            Title = Strings.CrashReport_Title,
            Content = new CrashReportDialogContent(report),
            PrimaryButtonText = Strings.CrashReport_Button_Reset,
            CloseButtonText = Strings.Generic_Close,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };

        DialogThemeHelper.ApplyThemeOverrides(dialog, _window.Content as FrameworkElement);

        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            // Fall back to a message box when the dialog can't show, for example while another dialog is open.
            _logger.LogWarning(ex, "Could not show the crash report dialog");
            var hwnd = WindowNative.GetWindowHandle(_window);
            var message = $"{Strings.CrashReport_Message}\n\n{report}";
            NativeMethods.MessageBox(hwnd, message, Strings.CrashReport_Title, NativeMethods.MB_ICONERROR | NativeMethods.MB_OK);
            return false;
        }
    }

    public async Task<string?> PickSingleFileAsync(IEnumerable<string> fileTypes)
    {
        try
        {
            var file = await CreateFilePicker(fileTypes).PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show the file picker");
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> PickMultipleFilesAsync(IEnumerable<string> fileTypes)
    {
        try
        {
            var files = await CreateFilePicker(fileTypes).PickMultipleFilesAsync();
            return files.Select(f => f.Path).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show the file picker");
            return [];
        }
    }

    private FileOpenPicker CreateFilePicker(IEnumerable<string> fileTypes)
    {
        var filePicker = new FileOpenPicker(_window.AppWindow.Id);
        foreach (var ext in fileTypes) filePicker.FileTypeFilter.Add(ext);
        return filePicker;
    }

    public async Task ShowSongInfoAsync(Song song)
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return;

        var viewModel = _viewModelFactory.Create<SongInfoViewModel>();
        await viewModel.LoadAsync(song);
        await ShowDialogAsync(new SongInfoDialog(viewModel) { XamlRoot = xamlRoot });
    }

    public async Task<SmartPlaylist?> ShowSmartPlaylistEditorAsync(SmartPlaylist? editing = null)
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return null;

        using var viewModel = _viewModelFactory.Create<SmartPlaylistEditorViewModel>();
        viewModel.Initialize(editing);
        var dialog = new SmartPlaylistEditorDialog(viewModel) { XamlRoot = xamlRoot };
        var result = await ShowDialogAsync(dialog);
        return result == ContentDialogResult.Primary ? viewModel.ResultPlaylist : null;
    }

    public async Task<bool> ShowFFmpegSetupDialogAsync(Func<Task<bool>> checkAction)
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return false;

        var viewModel = new FFmpegSetupViewModel(checkAction);
        var statusBlock = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void ShowStatus()
        {
            statusBlock.Text = viewModel.StatusText;
            statusBlock.Style = (Style)Application.Current.Resources[viewModel.Status switch
            {
                FFmpegSetupStatus.Checking => "FFmpegStatusCheckingTextStyle",
                FFmpegSetupStatus.Detected => "FFmpegStatusDetectedTextStyle",
                _ => "FFmpegStatusMissingTextStyle"
            }];
        }
        viewModel.PropertyChanged += (_, _) => ShowStatus();
        ShowStatus();

        var contentPanel = new StackPanel { Spacing = 12 };
        contentPanel.Children.Add(new TextBlock { Text = Strings.FFmpeg_SetupInstructions, TextWrapping = TextWrapping.Wrap });
        contentPanel.Children.Add(statusBlock);

        var dialog = new ContentDialog
        {
            Title = Strings.Settings_Dialog_FFmpegNotFound_Title,
            Content = contentPanel,
            PrimaryButtonText = Strings.Generic_Recheck,
            CloseButtonText = Strings.Generic_Cancel,
            XamlRoot = xamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            args.Cancel = !await viewModel.RecheckAsync();
            deferral.Complete();
        };

        var result = await ShowDialogAsync(dialog);
        return result == ContentDialogResult.Primary;
    }

    private bool TryGetXamlRoot(out XamlRoot? xamlRoot)
    {
        xamlRoot = _window.Content?.XamlRoot;
        return xamlRoot is not null;
    }

    /// <summary>
    ///     Shows <paramref name="dialog" /> in the app's theme. WinUI throws when another dialog is already open;
    ///     that is reported as <see cref="ContentDialogResult.None" />, the same as a cancel.
    /// </summary>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        DialogThemeHelper.ApplyThemeOverrides(dialog, _window.Content as FrameworkElement);
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not show dialog {Title}", dialog.Title);
            return ContentDialogResult.None;
        }
    }
}
