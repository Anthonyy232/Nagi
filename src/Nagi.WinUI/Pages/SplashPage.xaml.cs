using Microsoft.UI.Xaml.Controls;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Pages;

public sealed partial class SplashPage : Page, ICustomTitleBarProvider
{
    public SplashPage()
    {
        this.InitializeComponent();
        this.Loaded += (_, _) =>
        {
            LogoEntrance.Begin();
            TitleEntrance.Begin();
            StatusEntrance.Begin();
            if (!CompositionAnimationHelper.AreAnimationsEnabled)
            {
                LogoEntrance.SkipToFill();
                TitleEntrance.SkipToFill();
                StatusEntrance.SkipToFill();
            }
        };
    }

    public TitleBar GetAppTitleBarElement()
    {
        return AppTitleBar;
    }

}
