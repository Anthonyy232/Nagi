using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI;

public sealed partial class MainPage
{
    private string GetListeningPaneTitle(bool history) => history ? Strings.Listening_RecentlyPlayed : Strings.QueueFlyout_Title;
    private Visibility GetQueueDetailVisibility(bool history, bool enabled) => !history && enabled ? Visibility.Visible : Visibility.Collapsed;
    private async void HistoryEntry_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: RecentListenEntry entry })
        {
            args.Handled = true;
            await ViewModel.PlayHistoryNowCommand.ExecuteAsync(entry);
        }
    }

    private void PlayerOptions_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu) return;
        menu.Items.Clear();
        menu.Items.Add(new MenuFlyoutItem { Text = ViewModel.FavoriteActionText, Command = ViewModel.ToggleFavoriteCommand });
        var stops = new MenuFlyoutSubItem { Text = Strings.Listening_StopPlayback };
        PopulateStopOptions(stops.Items);
        menu.Items.Add(stops);
        var speeds = new MenuFlyoutSubItem { Text = Strings.Listening_PlaybackSpeed };
        foreach (var rate in PlaybackRates)
        {
            speeds.Items.Add(new RadioMenuFlyoutItem
            {
                Text = rate == 1.0 ? Strings.Listening_PlaybackSpeedNormal : string.Format(Strings.Listening_PlaybackSpeedFormat, rate),
                GroupName = "PlaybackSpeed",
                IsChecked = ViewModel.PlaybackRate == rate,
                Command = ViewModel.SetPlaybackRateCommand,
                CommandParameter = rate
            });
        }
        menu.Items.Add(speeds);
    }

    private static readonly double[] PlaybackRates = [0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0];

    private void TimerOptions_Opening(object sender, object e)
    {
        if (sender is MenuFlyout menu) PopulateStopOptions(menu.Items);
    }

    private void PopulateStopOptions(IList<MenuFlyoutItemBase> items)
    {
        items.Clear();
        items.Add(new MenuFlyoutItem { Text = Strings.Listening_StopAfterTrack, Command = ViewModel.StopAfterTrackCommand });
        items.Add(new MenuFlyoutItem { Text = Strings.Listening_StopAtQueueEnd, Command = ViewModel.StopAtQueueEndCommand });
        items.Add(new MenuFlyoutSeparator());
        foreach (var (minutes, label) in new[] { (15, Strings.Listening_Timer15), (30, Strings.Listening_Timer30), (60, Strings.Listening_Timer60) })
            items.Add(new MenuFlyoutItem { Text = label, Command = ViewModel.SetSleepTimerCommand, CommandParameter = minutes });
        items.Add(new MenuFlyoutItem { Text = Strings.Listening_CustomTimer, Command = ViewModel.SetCustomSleepTimerCommand });
        if (ViewModel.IsStopScheduled)
        {
            items.Add(new MenuFlyoutSeparator());
            items.Add(new MenuFlyoutItem { Text = Strings.Listening_CancelStop, Command = ViewModel.CancelScheduledStopCommand });
        }
    }

    private string GetListeningPaneToggleLabel(bool history) => history ? Strings.QueueFlyout_Title : Strings.Listening_RecentlyPlayed;
    private string GetListeningPaneToggleIcon(bool history) => history ? "\uE90B" : "\uE81C";
    private void ToggleListeningPane_Click(object sender, RoutedEventArgs e) => ViewModel.IsHistoryVisible = !ViewModel.IsHistoryVisible;

    private async void OnListeningShortcutInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is Control { IsHitTestVisible: true, IsEnabled: true } input &&
            input is TextBox or PasswordBox or RichEditBox ||
            Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count != 0) return;
        var list = (ContentFrame.Content as FrameworkElement)?.DataContext as SongListViewModelBase;
        if (list?.ToggleSelectedFavoriteCommand.CanExecute(null) == true)
        {
            args.Handled = true;
            await list.ToggleSelectedFavoriteCommand.ExecuteAsync(null);
        }
        else if (list?.HasSelectedSongs != true && ViewModel.ToggleFavoriteCommand.CanExecute(null))
        {
            args.Handled = true;
            await ViewModel.ToggleFavoriteCommand.ExecuteAsync(null);
        }
    }
}
