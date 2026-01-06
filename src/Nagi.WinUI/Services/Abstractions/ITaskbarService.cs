using System;

namespace Nagi.WinUI.Services.Abstractions;

public interface ITaskbarService
{
    void Initialize(nint windowHandle);
    void HandleWindowMessage(int msg, nint wParam, nint lParam);
    void UpdateIcons(nint previousIcon, nint playIcon, nint pauseIcon, nint nextIcon);
}