using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Core.Models;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Models;
using Nagi.WinUI.Resources;

namespace Nagi.WinUI.Services.Implementations;

public partial class UIService
{
    private static NumberBox CreateMinutesInput() => new()
    {
        Header = Strings.Listening_Minutes,
        Minimum = 1,
        Maximum = 1440,
        Value = 30,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
    };

    private static bool IsValidMinutes(double value) => value is >= 1 and <= 1440 && value == Math.Truncate(value);

    public async Task<(int Minutes, bool FinishTrack)?> ShowSleepTimerDialogAsync()
    {
        if (!TryGetXamlRoot(out var root)) return null;
        var minutes = CreateMinutesInput();
        var finish = new CheckBox { Content = Strings.Listening_FinishTrack, IsChecked = true };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(minutes);
        content.Children.Add(finish);
        var dialog = new ContentDialog
        {
            Title = Strings.Listening_SleepTimer,
            Content = content,
            PrimaryButtonText = Strings.Generic_OK,
            CloseButtonText = Strings.Generic_Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root
        };
        minutes.ValueChanged += (_, _) => dialog.IsPrimaryButtonEnabled = IsValidMinutes(minutes.Value);
        DialogThemeHelper.ApplyThemeOverrides(dialog);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? ((int)minutes.Value, finish.IsChecked == true) : null;
    }
}
