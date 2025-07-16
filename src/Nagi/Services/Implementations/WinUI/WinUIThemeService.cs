using Microsoft.UI.Xaml;
using Nagi.Services.Abstractions;
using System;

namespace Nagi.Services.Implementations.WinUI;

public class WinUIThemeService : IThemeService {
    private readonly App _app;

    public WinUIThemeService(App app) {
        _app = app ?? throw new ArgumentNullException(nameof(app));
    }

    public void ApplyTheme(ElementTheme theme) {
        _app.ApplyTheme(theme);
    }

    public void ReapplyCurrentDynamicTheme() {
        _app.ReapplyCurrentDynamicTheme();
    }
}