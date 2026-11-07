using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Core.Models;
using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Services.Implementations;

public partial class UIService
{
    private static NumberBox CreateMinutesInput(double minutes) => new()
    {
        Header = Strings.Listening_Minutes,
        Minimum = 1,
        Maximum = PlaybackMixLimits.MaxMinutes,
        Value = minutes,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
    };

    public async Task<PlaybackMixOptions?> ShowPlayForDialogAsync()
    {
        if (!TryGetXamlRoot(out var root)) return null;
        var viewModel = new PlayForDialogViewModel();
        var minutes = CreateMinutesInput(viewModel.Minutes);
        var mode = new ComboBox
        {
            Header = Strings.Listening_ShuffleMode,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = viewModel.ModeOptions,
            DisplayMemberPath = nameof(SettingOption<ShuffleMode>.DisplayName),
            SelectedItem = viewModel.SelectedMode
        };
        var avoid = new NumberBox
        {
            Header = Strings.Listening_AvoidRecentDays,
            Minimum = 0,
            Maximum = PlaybackMixLimits.MaxAvoidRecentDays,
            Value = viewModel.AvoidRecentDays,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        var more = new Expander
        {
            Header = Strings.Listening_MoreOptions,
            Content = avoid,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
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

        void Refresh()
        {
            more.Visibility = viewModel.ShowsAvoidRecentDays ? Visibility.Visible : Visibility.Collapsed;
            description.Text = viewModel.ModeDescription;
            description.Visibility = description.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = viewModel.CanAccept;
        }

        mode.SelectionChanged += (_, _) =>
        {
            viewModel.SelectedMode = (SettingOption<ShuffleMode>)mode.SelectedItem;
            Refresh();
        };
        minutes.ValueChanged += (_, _) =>
        {
            viewModel.Minutes = minutes.Value;
            Refresh();
        };
        avoid.ValueChanged += (_, _) =>
        {
            viewModel.AvoidRecentDays = avoid.Value;
            Refresh();
        };
        Refresh();
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary ? viewModel.ToOptions() : null;
    }

    public async Task<(int Minutes, bool FinishTrack)?> ShowSleepTimerDialogAsync()
    {
        if (!TryGetXamlRoot(out var root)) return null;
        var viewModel = new SleepTimerDialogViewModel();
        var minutes = CreateMinutesInput(viewModel.Minutes);
        var finish = new CheckBox { Content = Strings.Listening_FinishTrack, IsChecked = viewModel.FinishTrack };
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
        minutes.ValueChanged += (_, _) =>
        {
            viewModel.Minutes = minutes.Value;
            dialog.IsPrimaryButtonEnabled = viewModel.CanAccept;
        };
        finish.Click += (_, _) => viewModel.FinishTrack = finish.IsChecked == true;
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary
            ? ((int)viewModel.Minutes, viewModel.FinishTrack)
            : null;
    }
}
