using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.Presentation.Resources;
using Nagi.WinUI.Pages;

namespace Nagi.WinUI;

public sealed partial class MainPage
{
    private Visibility GetQueueDetailVisibility(bool history, bool enabled) => !history && enabled ? Visibility.Visible : Visibility.Collapsed;

    private void PlayerOptions_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu) return;
        menu.Items.Clear();
        menu.Items.Add(new MenuFlyoutItem { Text = ViewModel.FavoriteActionText, Command = ViewModel.ToggleFavoriteCommand });
        var stops = new MenuFlyoutSubItem { Text = Strings.Listening_StopPlayback };
        PopulateStopOptions(stops.Items);
        menu.Items.Add(stops);
        var speeds = new MenuFlyoutSubItem { Text = Strings.Listening_PlaybackSpeed };
        foreach (var option in ViewModel.PlaybackRateOptions)
        {
            speeds.Items.Add(new RadioMenuFlyoutItem
            {
                Text = option.DisplayName,
                GroupName = "PlaybackSpeed",
                IsChecked = ViewModel.PlaybackRate == option.Rate,
                Command = ViewModel.SetPlaybackRateCommand,
                CommandParameter = option.Rate
            });
        }
        menu.Items.Add(speeds);
    }

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
        foreach (var option in ViewModel.SleepTimerOptions)
            items.Add(new MenuFlyoutItem { Text = option.DisplayName, Command = ViewModel.SetSleepTimerCommand, CommandParameter = option.Minutes });
        items.Add(new MenuFlyoutItem { Text = Strings.Listening_CustomTimer, Command = ViewModel.SetCustomSleepTimerCommand });
        if (ViewModel.IsStopScheduled)
        {
            items.Add(new MenuFlyoutSeparator());
            items.Add(new MenuFlyoutItem { Text = Strings.Listening_CancelStop, Command = ViewModel.CancelScheduledStopCommand });
        }
    }

    private void ToggleListeningPane_Click(object sender, RoutedEventArgs e) => ViewModel.IsHistoryVisible = !ViewModel.IsHistoryVisible;

    private async void OnListeningShortcutInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsShortcutBlocked(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject)) return;

        var activeList = (ContentFrame.Content as SongListPage)?.SongList;
        if (ViewModel.GetFavoriteShortcutCommand(activeList) is not { } command) return;
        args.Handled = true;
        await command.ExecuteAsync(null);
    }
}
