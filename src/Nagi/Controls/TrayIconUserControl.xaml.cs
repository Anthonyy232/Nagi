using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Nagi.ViewModels;

namespace Nagi.Controls;

/// <summary>
/// A UserControl that hosts the application's taskbar icon and its context menu.
/// Its primary role is to initialize the associated ViewModel and provide the
/// H.NotifyIcon.TaskbarIcon element from its XAML to the ViewModel.
/// </summary>
public sealed partial class TrayIconUserControl : UserControl {
    /// <summary>
    /// Gets the ViewModel associated with this control.
    /// </summary>
    public TrayIconViewModel ViewModel { get; }

    public TrayIconUserControl() {
        this.InitializeComponent();
        ViewModel = App.Services.GetRequiredService<TrayIconViewModel>();

        Loaded += async (sender, args) => {
            // The ViewModel requires the actual TaskbarIcon UI element to function.
            // This is only available after the control has been loaded into the visual tree.
            await ViewModel.InitializeAsync(this.AppTrayIcon);
        };
    }
}