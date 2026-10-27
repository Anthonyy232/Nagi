using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Models;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<SettingsViewModel>();
        DataContext = ViewModel;
    }

    public SettingsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.OpenAsync();
        Bindings.Update();
    }

    private void ProvidersListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.Any(i => i is ServiceProviderSettingViewModel { IsPinnedFirst: true })) e.Cancel = true;
    }

    private async void AudioOutput_DropDownOpened(object sender, object e) =>
        await ViewModel.Audio.RefreshAudioOutputDevicesCommand.ExecuteAsync(null);

    private async void AudioOutput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: AudioOutputDeviceInfo device })
            await ViewModel.Audio.SelectAudioOutputDeviceCommand.ExecuteAsync(device);
    }
}
