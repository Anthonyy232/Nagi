
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.WinUI.Services.Abstractions;
using WinRT.Interop;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     An implementation of the IUIService that uses WinUI 3 controls.
/// </summary>
public class UIService : IUIService {
    public async Task<bool> ShowConfirmationDialogAsync(string title, string content, string primaryButtonText,
        string? closeButtonText) {
        if (!TryGetXamlRoot(out var xamlRoot)) return false;

        var dialog = new ContentDialog {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = closeButtonText,
            XamlRoot = xamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task<string?> PickSingleFolderAsync() {
        if (App.RootWindow is null) return null;

        var folderPicker = new FolderPicker();
        var hwnd = WindowNative.GetWindowHandle(App.RootWindow);
        InitializeWithWindow.Initialize(folderPicker, hwnd);
        folderPicker.FileTypeFilter.Add("*");

        var selectedFolder = await folderPicker.PickSingleFolderAsync();
        return selectedFolder?.Path;
    }

    public async Task OpenFolderInExplorerAsync(string filePath) {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        var folderPath = Path.GetDirectoryName(filePath);
        if (folderPath is null) return;

        var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
        await Launcher.LaunchFolderAsync(folder);
    }

    public async Task<UpdateDialogResult> ShowUpdateDialogAsync(string title, string content, string primaryButtonText,
        string secondaryButtonText, string closeButtonText) {
        if (!TryGetXamlRoot(out var xamlRoot))
            return UpdateDialogResult.RemindLater;

        var dialog = new ContentDialog {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            SecondaryButtonText = secondaryButtonText,
            CloseButtonText = closeButtonText,
            XamlRoot = xamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();

        return result switch {
            ContentDialogResult.Primary => UpdateDialogResult.Install,
            ContentDialogResult.Secondary => UpdateDialogResult.RemindLater,
            _ => UpdateDialogResult.Skip
        };
    }


    public async Task ShowMessageDialogAsync(string title, string message) {
        if (!TryGetXamlRoot(out var xamlRoot)) return;

        var dialog = new ContentDialog {
            Title = title,
            Content = message,
            PrimaryButtonText = "OK",
            XamlRoot = xamlRoot
        };

        await dialog.ShowAsync();
    }

    /// <summary>
    ///     Safely retrieves the XamlRoot from the main window's content.
    /// </summary>
    private bool TryGetXamlRoot(out XamlRoot? xamlRoot) {
        xamlRoot = App.RootWindow?.Content?.XamlRoot;
        return xamlRoot is not null;
    }
}