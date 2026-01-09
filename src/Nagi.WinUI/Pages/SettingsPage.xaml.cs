using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Nagi.WinUI.ViewModels;
using Nagi.Core.Models;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page for configuring application settings.
/// </summary>
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

        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModel.IsFetchOnlineMetadataEnabled):
                MetadataSettingsExpander.IsExpanded = ViewModel.IsFetchOnlineMetadataEnabled;
                break;
            case nameof(ViewModel.IsFetchOnlineLyricsEnabled):
                LyricsSettingsExpander.IsExpanded = ViewModel.IsFetchOnlineLyricsEnabled;
                break;
            case nameof(ViewModel.IsLastFmConnected):
                LastFmSettingsExpander.IsExpanded = ViewModel.IsLastFmConnected;
                break;
        }
    }

    public SettingsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _logger.LogDebug("Navigated to SettingsPage. Loading settings...");
        try
        {
            await ViewModel.LoadSettingsAsync();
            _logger.LogDebug("Settings loaded successfully.");

            // Set initial expander states after settings are loaded (no animation)
            MetadataSettingsExpander.IsExpanded = ViewModel.IsFetchOnlineMetadataEnabled;
            LyricsSettingsExpander.IsExpanded = ViewModel.IsFetchOnlineLyricsEnabled;
            LastFmSettingsExpander.IsExpanded = ViewModel.IsLastFmConnected;

            // Subscribe to property changes for reactive updates (these will animate)
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings.");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogDebug("Navigating away from SettingsPage.");
    }

    private void ProvidersListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        // Prevent dragging MusicBrainz metadata provider
        if (e.Items.Any(i => i is ServiceProviderSettingViewModel { Id: ServiceProviderIds.MusicBrainz }))
        {
            e.Cancel = true;
        }
    }
}
