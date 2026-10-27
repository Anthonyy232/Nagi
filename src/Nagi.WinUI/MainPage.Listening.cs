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
