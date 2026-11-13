using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Shell;

namespace Nagi.WinUI.Controls;

/// <summary>
///     Hosts the tray icon in the main page, whose theme its context menu follows. The control itself is invisible.
/// </summary>
public sealed partial class TrayIconUserControl : UserControl, IDisposable
{
    private bool _isDisposed;

    public TrayIconUserControl(TrayIconViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public TrayIconViewModel ViewModel { get; }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        AppTrayIcon.Dispose();
    }
}
