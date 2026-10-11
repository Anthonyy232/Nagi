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
        ListeningMenuFlyoutHelper.PopulateMixes((MenuFlyout)sender, ViewModel.PlayQueueMixCommand);
}
