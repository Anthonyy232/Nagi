using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.WinUI.ViewModels;
using Nagi.Core.Models;

namespace Nagi.WinUI.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly ILogger<SettingsPage> _logger;

    public SettingsPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<SettingsViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<SettingsPage>>();
        DataContext = ViewModel;
        _logger.LogDebug("SettingsPage initialized.");
    }

    public SettingsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _logger.LogDebug("Navigated to SettingsPage. Loading settings...");
        try
        {
            await ViewModel.LoadSettingsAsync();
            await ViewModel.RefreshAudioOutputDevicesCommand.ExecuteAsync(null);
            _logger.LogDebug("Settings loaded successfully.");

            Bindings.Update();
            _ = ViewModel.LoadRomanizationPacksAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings.");
        }
    }

    private void ProvidersListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.Any(i => i is ServiceProviderSettingViewModel { Id: ServiceProviderIds.MusicBrainz }))
        {
            e.Cancel = true;
        }
    }

    private async void AudioOutput_DropDownOpened(object sender, object e) =>
        await ViewModel.RefreshAudioOutputDevicesCommand.ExecuteAsync(null);

    private async void AudioOutput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: AudioOutputDeviceInfo device })
            await ViewModel.SelectAudioOutputDeviceCommand.ExecuteAsync(device);
    }
}
