using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Services.Abstractions;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Nagi.Services.Implementations.WinUI;

/// <summary>
/// An implementation of the IUIService that uses WinUI 3 controls.
/// </summary>
public class WinUIUIService : IUIService {
    private readonly Window _rootWindow;

    public WinUIUIService(Window rootWindow) {
        _rootWindow = rootWindow ?? throw new ArgumentNullException(nameof(rootWindow));
    }

    public async Task<bool> ShowConfirmationDialogAsync(string title, string content, string primaryButtonText, string? closeButtonText) {
        if (!TryGetXamlRoot(out var xamlRoot)) return false;

        var dialog = new ContentDialog {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = closeButtonText,
            XamlRoot = xamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task<string?> PickSingleFolderAsync() {
        var folderPicker = new FolderPicker();

        // The folder picker needs to be associated with a window handle to display.
        IntPtr hwnd = WindowNative.GetWindowHandle(_rootWindow);
        InitializeWithWindow.Initialize(folderPicker, hwnd);
        folderPicker.FileTypeFilter.Add("*");

        StorageFolder? selectedFolder = await folderPicker.PickSingleFolderAsync();
        return selectedFolder?.Path;
    }

    public async Task OpenFolderInExplorerAsync(string filePath) {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        string? folderPath = Path.GetDirectoryName(filePath);
        if (folderPath is null) return;

        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
        await Launcher.LaunchFolderAsync(folder);
    }

    public async Task<UpdateDialogResult> ShowUpdateDialogAsync(string title, string content, string primaryButtonText, string secondaryButtonText, string closeButtonText) {
        if (!TryGetXamlRoot(out var xamlRoot)) {
            // If the UI is not ready, default to a safe, non-blocking action.
            return UpdateDialogResult.RemindLater;
        }

        var dialog = new ContentDialog {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            SecondaryButtonText = secondaryButtonText,
            CloseButtonText = closeButtonText,
            XamlRoot = xamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };

        ContentDialogResult result = await dialog.ShowAsync();

        return result switch {
            ContentDialogResult.Primary => UpdateDialogResult.Install,
            ContentDialogResult.Secondary => UpdateDialogResult.RemindLater,
            // Covers ContentDialogResult.None, which occurs when the user clicks the close button (X) or the designated close button.
            _ => UpdateDialogResult.Skip,
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
    /// Safely retrieves the XamlRoot from the main window's content.
    /// </summary>
    /// <param name="xamlRoot">The retrieved XamlRoot, or null if not available.</param>
    /// <returns>True if the XamlRoot was successfully retrieved, otherwise false.</returns>
    private bool TryGetXamlRoot(out XamlRoot? xamlRoot) {
        xamlRoot = _rootWindow.Content?.XamlRoot;
        return xamlRoot is not null;
    }
}