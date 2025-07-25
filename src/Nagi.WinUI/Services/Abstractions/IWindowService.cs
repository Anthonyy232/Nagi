using System;
using Microsoft.UI.Windowing;

namespace Nagi.WinUI.Services.Abstractions;

/// <summary>
/// Abstracts interactions with the main application window.
/// </summary>
public interface IWindowService {
    event Action<AppWindowClosingEventArgs>? Closing;
    event Action<AppWindowChangedEventArgs>? VisibilityChanged;

    bool IsVisible { get; }
    bool IsExiting { get; set; }

    void Hide();
    void ShowAndActivate();
    void Close();
}