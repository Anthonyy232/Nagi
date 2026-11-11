using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Playback;
using Nagi.Presentation.Common;
using Nagi.Presentation.Resources;
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

    private string Milliseconds(double value) => ResourceFormatter.Format(Strings.SettingsPage_Milliseconds_Format, value);

    private string Decibels(float value) => ResourceFormatter.Format(Strings.SettingsPage_Decibels_Format, value);

    private string Version(string version) => ResourceFormatter.Format(Strings.SettingsPage_About_Version_Format, version);

    private async void AudioOutput_DropDownOpened(object sender, object e) =>
        await ViewModel.Audio.RefreshAudioOutputDevicesCommand.ExecuteAsync(null);

    private async void AudioOutput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: AudioOutputDeviceInfo device })
            await ViewModel.Audio.SelectAudioOutputDeviceCommand.ExecuteAsync(device);
    }
}
