using System;
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
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Dialogs;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;
using WinRT.Interop;

namespace Nagi.WinUI.Services.Implementations;

public partial class UIService : IUIService
{
    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;

    private readonly IWin32InteropService _win32InteropService;
    private readonly IViewModelFactory _viewModelFactory;
    private readonly ILogger<UIService> _logger;
    private readonly MainWindow _window;

    public UIService(MainWindow window, IWin32InteropService win32InteropService, IViewModelFactory viewModelFactory,
        ILogger<UIService> logger)
    {
        _window = window;
        _win32InteropService = win32InteropService;
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

        DialogThemeHelper.ApplyThemeOverrides(dialog);
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task<string?> PickSingleFolderAsync()
    {
        var folderPicker = new FolderPicker(_window.AppWindow.Id);

        var selectedFolder = await folderPicker.PickSingleFolderAsync();
        return selectedFolder?.Path;
    }

    public async Task OpenFolderInExplorerAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        var folderPath = Path.GetDirectoryName(filePath);
        if (folderPath is null) return;

        var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
        await Launcher.LaunchFolderAsync(folder);
    }

    public async Task<bool> OpenUriAsync(Uri uri) => await Launcher.LaunchUriAsync(uri);

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

        DialogThemeHelper.ApplyThemeOverrides(dialog);
        await dialog.ShowAsync();
    }

    public async Task<CrashReportResult> ShowCrashReportDialogAsync(string title, string introduction, string logContent, string githubUrl)
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return CrashReportResult.Close;

        var dialogContent = new CrashReportDialogContent
        {
            Introduction = introduction,
            LogContent = logContent,
            GitHubUrl = githubUrl
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = dialogContent,
            PrimaryButtonText = Strings.CrashReport_Button_Reset,
            CloseButtonText = Strings.Generic_Close,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };

        DialogThemeHelper.ApplyThemeOverrides(dialog);

        try
        {
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? CrashReportResult.Reset : CrashReportResult.Close;
        }
        catch (Exception)
        {
            // Fallback to Win32 MessageBox if ContentDialog fails (e.g. another dialog is open)
            var hwnd = WindowNative.GetWindowHandle(_window);
            var message = $"{introduction}\n\n{logContent}";
            _win32InteropService.ShowMessageBox(hwnd, message, title, MB_ICONERROR | MB_OK);
            return CrashReportResult.Close;
        }
    }

    public async Task<string?> PickSingleFileAsync(IEnumerable<string> fileTypes)
    {
        var filePicker = new FileOpenPicker(_window.AppWindow.Id);

        foreach (var ext in fileTypes)
        {
            filePicker.FileTypeFilter.Add(ext);
        }

        var file = await filePicker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<IReadOnlyList<string>> PickOpenMultipleFilesAsync(IEnumerable<string> fileTypes)
    {
        var filePicker = new FileOpenPicker(_window.AppWindow.Id);

        foreach (var ext in fileTypes)
        {
            filePicker.FileTypeFilter.Add(ext);
        }

        var files = await filePicker.PickMultipleFilesAsync();
        return files.Select(f => f.Path).ToList();
    }

    public async Task<SmartPlaylist?> ShowSmartPlaylistEditorAsync(SmartPlaylist? editing = null)
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return null;

        using var viewModel = _viewModelFactory.Create<SmartPlaylistEditorViewModel>();
        viewModel.Initialize(editing);
        var dialog = new SmartPlaylistEditorDialog(viewModel) { XamlRoot = xamlRoot };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? viewModel.ResultPlaylist : null;
    }

    public async Task<bool> ShowFFmpegSetupDialogAsync(string title, string instructions, Func<Task<bool>> checkAction)
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return false;

        var contentPanel = new StackPanel { Spacing = 12 };

        var instructionsBlock = new TextBlock
        {
            Text = instructions,
            TextWrapping = TextWrapping.Wrap
        };
        contentPanel.Children.Add(instructionsBlock);

        var statusBlock = new TextBlock
        {
            Text = Strings.FFmpeg_Status_NotDetected,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange)
        };
        contentPanel.Children.Add(statusBlock);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = contentPanel,
            PrimaryButtonText = Strings.Generic_Recheck,
            CloseButtonText = Strings.Generic_Cancel,
            XamlRoot = xamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };

        dialog.PrimaryButtonClick += async (sender, args) =>
        {
            var deferral = args.GetDeferral();

            statusBlock.Text = Strings.FFmpeg_Status_Checking;
            statusBlock.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);

            try
            {
                var isInstalled = await checkAction();

                if (isInstalled)
                {
                    statusBlock.Text = Strings.FFmpeg_Status_Detected;
                    statusBlock.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
                }
                else
                {
                    args.Cancel = true;
                    statusBlock.Text = Strings.FFmpeg_Status_StillNotDetected;
                    statusBlock.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
                }
            }
            catch
            {
                args.Cancel = true;
                statusBlock.Text = Strings.FFmpeg_Status_Error;
                statusBlock.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
            }
            finally
            {
                deferral.Complete();
            }
        };

        DialogThemeHelper.ApplyThemeOverrides(dialog);
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private bool TryGetXamlRoot(out XamlRoot? xamlRoot)
    {
        xamlRoot = _window.Content?.XamlRoot;
        return xamlRoot is not null;
    }
}
