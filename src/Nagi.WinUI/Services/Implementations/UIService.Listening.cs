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

    public async Task<PlaybackMixOptions?> ShowPlayForDialogAsync()
    {
        if (!TryGetXamlRoot(out var root)) return null;
        var minutes = CreateMinutesInput();
        var mode = new ComboBox
        {
            Header = Strings.Listening_ShuffleMode,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var value in Enum.GetValues<ShuffleMode>()) mode.Items.Add(ListeningMenuFlyoutHelper.GetModeName(value));
        mode.SelectedIndex = 0;
        var avoid = new NumberBox
        {
            Header = Strings.Listening_AvoidRecentDays,
            Minimum = 0,
            Maximum = 365,
            Value = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        var more = new Expander
        {
            Header = Strings.Listening_MoreOptions,
            Content = avoid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Visibility = Visibility.Collapsed };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = Strings.Listening_PlayForDescription, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });
        content.Children.Add(minutes);
        content.Children.Add(mode);
        content.Children.Add(description);
        content.Children.Add(more);
        var dialog = new ContentDialog
        {
            Title = Strings.Listening_PlayFor,
            Content = content,
            PrimaryButtonText = Strings.QueuePanel_Play,
            CloseButtonText = Strings.Generic_Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root
        };
        void Validate() => dialog.IsPrimaryButtonEnabled = IsValidMinutes(minutes.Value)
            && (mode.SelectedIndex != (int)ShuffleMode.MoreVariety
                || avoid.Value is >= 0 and <= 365 && avoid.Value == Math.Truncate(avoid.Value));
        mode.SelectionChanged += (_, _) =>
        {
            more.Visibility = mode.SelectedIndex == (int)ShuffleMode.MoreVariety ? Visibility.Visible : Visibility.Collapsed;
            description.Text = ListeningMenuFlyoutHelper.GetModeDescription((ShuffleMode)mode.SelectedIndex);
            description.Visibility = description.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Validate();
        };
        minutes.ValueChanged += (_, _) => Validate();
        avoid.ValueChanged += (_, _) => Validate();
        DialogThemeHelper.ApplyThemeOverrides(dialog);
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? new PlaybackMixOptions((int)minutes.Value, (ShuffleMode)mode.SelectedIndex,
                mode.SelectedIndex == (int)ShuffleMode.MoreVariety ? (int)avoid.Value : 1) : null;
    }

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
