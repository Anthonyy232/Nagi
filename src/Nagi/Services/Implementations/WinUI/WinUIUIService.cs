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

public class WinUIUIService : IUIService {
    private readonly Window _rootWindow;

    public WinUIUIService(Window rootWindow) {
        _rootWindow = rootWindow ?? throw new ArgumentNullException(nameof(rootWindow));
    }

    public async Task<bool> ShowConfirmationDialogAsync(string title, string content, string primaryButtonText, string? closeButtonText) {
        if (_rootWindow.Content?.XamlRoot is not { } xamlRoot) return false;

        var dialog = new ContentDialog {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = closeButtonText,
            XamlRoot = xamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task<string?> PickSingleFolderAsync() {
        var folderPicker = new FolderPicker();
        IntPtr hwnd = WindowNative.GetWindowHandle(_rootWindow);
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
}