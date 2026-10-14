using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Controls;

public sealed partial class TrayIconUserControl : UserControl, IDisposable
{
    private bool _isDisposed;

    public TrayIconUserControl()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<TrayIconViewModel>();
    }

    public TrayIconViewModel ViewModel { get; }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        AppTrayIcon.Dispose();
    }
}
