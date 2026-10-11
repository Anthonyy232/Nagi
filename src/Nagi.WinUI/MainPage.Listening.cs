using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Resources;
using Nagi.WinUI.ViewModels;
using Windows.System;

namespace Nagi.WinUI;

public sealed partial class MainPage
{

    private void ShuffleOptions_Opening(object sender, object e) =>
        ListeningMenuFlyoutHelper.PopulateMixes((MenuFlyout)sender, ViewModel.PlayQueueMixCommand, ViewModel.PlayQueueForCommand);

    private void PlayerOptions_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu) return;
        menu.Items.Clear();
        var stops = new MenuFlyoutSubItem { Text = Strings.Listening_StopPlayback };
        PopulateStopOptions(stops.Items);
        menu.Items.Add(stops);
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
        foreach (var (minutes, label) in new[] { (15, Strings.Listening_Timer15), (30, Strings.Listening_Timer30), (60, Strings.Listening_Timer60) })
            items.Add(new MenuFlyoutItem { Text = label, Command = ViewModel.SetSleepTimerCommand, CommandParameter = minutes });
        items.Add(new MenuFlyoutItem { Text = Strings.Listening_CustomTimer, Command = ViewModel.SetCustomSleepTimerCommand });
        if (ViewModel.IsStopScheduled)
        {
            items.Add(new MenuFlyoutSeparator());
            items.Add(new MenuFlyoutItem { Text = Strings.Listening_CancelStop, Command = ViewModel.CancelScheduledStopCommand });
        }
    }
}
