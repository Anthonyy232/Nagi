using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Core.Models;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;

namespace Nagi.WinUI.Services.Implementations;

public partial class UIService
{
    private static NumberBox CreateMinutesInput() => new()
    {
        Header = Strings.Listening_Minutes,
        Minimum = 1,
        Maximum = PlaybackMixLimits.MaxMinutes,
        Value = ListeningMinutes.Default,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
    };

    public async Task<PlaybackMixOptions?> ShowPlayForDialogAsync()
    {
        if (!TryGetXamlRoot(out var root)) return null;
        var minutes = CreateMinutesInput();
        var mode = new ComboBox
        {
            Header = Strings.Listening_ShuffleMode,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var value in Enum.GetValues<ShuffleMode>()) mode.Items.Add(ShuffleModeText.GetName(value));
        mode.SelectedIndex = 0;
        var avoid = new NumberBox
        {
            Header = Strings.Listening_AvoidRecentDays,
            Minimum = 0,
            Maximum = PlaybackMixLimits.MaxAvoidRecentDays,
            Value = PlaybackMixLimits.DefaultAvoidRecentDays,
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
        void Validate() => dialog.IsPrimaryButtonEnabled = ListeningMinutes.IsValid(minutes.Value)
            && (!PlaybackMixOptions.UsesAvoidRecentDays((ShuffleMode)mode.SelectedIndex)
                || PlaybackMixOptions.IsValidAvoidRecentDays(avoid.Value));
        mode.SelectionChanged += (_, _) =>
        {
            more.Visibility = PlaybackMixOptions.UsesAvoidRecentDays((ShuffleMode)mode.SelectedIndex)
                ? Visibility.Visible : Visibility.Collapsed;
            description.Text = ShuffleModeText.GetDescription((ShuffleMode)mode.SelectedIndex);
            description.Visibility = description.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Validate();
        };
        minutes.ValueChanged += (_, _) => Validate();
        avoid.ValueChanged += (_, _) => Validate();
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary
            ? new PlaybackMixOptions((int)minutes.Value, (ShuffleMode)mode.SelectedIndex,
                PlaybackMixOptions.UsesAvoidRecentDays((ShuffleMode)mode.SelectedIndex)
                    ? (int)avoid.Value : PlaybackMixLimits.DefaultAvoidRecentDays) : null;
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
        minutes.ValueChanged += (_, _) => dialog.IsPrimaryButtonEnabled = ListeningMinutes.IsValid(minutes.Value);
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary ? ((int)minutes.Value, finish.IsChecked == true) : null;
    }
}
