using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Controls;

public sealed partial class TrayIconUserControl : UserControl, IDisposable
{
    private bool _isDisposed;

    public TrayIconUserControl()
    {
        InitializeComponent();

        // Retrieve the shared ViewModel instance from the application's service provider.
        ViewModel = App.Services!.GetRequiredService<TrayIconViewModel>();

        Loaded += OnLoaded;
    }

    /// <summary>
    ///     Gets the ViewModel for this control, which manages the tray icon's logic and state.
    /// </summary>
    public TrayIconViewModel ViewModel { get; }

    /// <summary>
    ///     Disposes the TaskbarIcon control to prevent the "Exception Processing Message 0xc0000005"
    ///     error that occurs when the application exits.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;

        AppTrayIcon?.Dispose();
        ViewModel?.Dispose();

        _isDisposed = true;
    }

    /// <summary>
    ///     Initializes the ViewModel when the control is loaded into the visual tree.
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The ViewModel initializes itself and hooks into application events.
        await ViewModel.InitializeAsync();
    }
}