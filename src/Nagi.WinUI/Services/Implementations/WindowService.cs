using H.NotifyIcon;
using H.NotifyIcon.EfficiencyMode;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;
using System;

namespace Nagi.WinUI.Services.Implementations;

public class WindowService : IWindowService, IDisposable {
    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly IWin32InteropService _win32InteropService;

    public event Action<AppWindowClosingEventArgs>? Closing;
    public event Action<AppWindowChangedEventArgs>? VisibilityChanged;

    public bool IsVisible => _appWindow.IsVisible;
    public bool IsExiting { get; set; }

    public WindowService(Window window, IWin32InteropService win32InteropService) {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _appWindow = window.AppWindow;
        _win32InteropService = win32InteropService ?? throw new ArgumentNullException(nameof(win32InteropService));

        _appWindow.Closing += OnAppWindowClosing;
        _appWindow.Changed += OnAppWindowChanged;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args) => Closing?.Invoke(args);
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) {
        if (args.DidVisibilityChange) {
            VisibilityChanged?.Invoke(args);
        }
    }

    public void Hide() => _window.Hide(enableEfficiencyMode: true);

    public void ShowAndActivate() {
        EfficiencyModeUtilities.SetEfficiencyMode(false);
        WindowActivator.ShowAndActivate(_window, _win32InteropService);
    }

    public void Close() => _window.Close();

    public void Dispose() {
        _appWindow.Closing -= OnAppWindowClosing;
        _appWindow.Changed -= OnAppWindowChanged;
        GC.SuppressFinalize(this);
    }
}