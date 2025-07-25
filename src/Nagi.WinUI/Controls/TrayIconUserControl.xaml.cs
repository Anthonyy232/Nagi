using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Controls;

public sealed partial class TrayIconUserControl : UserControl {
    /// <summary>
    /// Gets the ViewModel for this control, which manages the tray icon's logic and state.
    /// </summary>
    public TrayIconViewModel ViewModel { get; }

    public TrayIconUserControl() {
        InitializeComponent();

        // Retrieve the shared ViewModel instance from the application's service provider.
        ViewModel = App.Services!.GetRequiredService<TrayIconViewModel>();

        Loaded += OnLoaded;
    }

    /// <summary>
    /// Initializes the ViewModel when the control is loaded into the visual tree.
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e) {
        // The ViewModel initializes itself and hooks into application events.
        await ViewModel.InitializeAsync();
    }
}