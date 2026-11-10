using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Playback;
using Nagi.Presentation.Settings;

namespace Nagi.WinUI.Pages;

public sealed partial class SettingsPage : ViewModelPage
{
    public SettingsPage()
    {
        InitializeComponent();
        ViewModel = GetViewModel<SettingsViewModel>();
    }

    public SettingsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.OpenAsync();
        Bindings.Update();
    }

    private async void AudioOutput_DropDownOpened(object sender, object e) =>
        await ViewModel.Audio.RefreshAudioOutputDevicesCommand.ExecuteAsync(null);

    private async void AudioOutput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: AudioOutputDeviceInfo device })
            await ViewModel.Audio.SelectAudioOutputDeviceCommand.ExecuteAsync(device);
    }
}
