using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.ViewModels;

/// <summary>
///     Settings section listing downloadable lyric romanization packs.
/// </summary>
public partial class RomanizationPacksSettingsViewModel : SettingsSectionViewModel
{
    private readonly IRomanizationPackManager _romanizationPackManager;
    private CancellationTokenSource? _romanizationPackCts;

    public RomanizationPacksSettingsViewModel(
        IRomanizationPackManager romanizationPackManager,
        IDispatcherService dispatcherService,
        ILogger<RomanizationPacksSettingsViewModel> logger)
        : base(dispatcherService, logger)
    {
        _romanizationPackManager = romanizationPackManager;
    }

    [ObservableProperty] public partial bool IsLoadingRomanizationPacks { get; set; }
    [ObservableProperty] public partial string? RomanizationPacksStatus { get; set; }
    public ObservableRangeCollection<RomanizationPackViewModel> RomanizationPacks { get; } = new();

    protected override void OnDisposing()
    {
        _romanizationPackCts?.Cancel();
        _romanizationPackCts?.Dispose();
    }

    [RelayCommand]
    private Task RefreshRomanizationPacksAsync()
    {
        return LoadRomanizationPacksAsync(forceRefresh: true);
    }

    public Task LoadRomanizationPacksAsync()
    {
        return LoadRomanizationPacksAsync(forceRefresh: false);
    }

    private async Task LoadRomanizationPacksAsync(bool forceRefresh)
    {
        if (_isDisposed) return;
        _romanizationPackCts?.Cancel();
        _romanizationPackCts?.Dispose();
        _romanizationPackCts = new CancellationTokenSource();
        var token = _romanizationPackCts.Token;

        IsLoadingRomanizationPacks = true;
        RomanizationPacksStatus = null;

        try
        {
            var packs = await _romanizationPackManager.GetAvailablePacksAsync(forceRefresh, token).ConfigureAwait(false);
            _dispatcherService.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || _isDisposed) return;
                RomanizationPacks.ReplaceRange(packs.Select(p => new RomanizationPackViewModel(p)));
                RomanizationPacksStatus = RomanizationPacks.Count == 0
                    ? Strings.SettingsPage_RomanizationPacks_None
                    : null;
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh romanization packs.");
            _dispatcherService.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || _isDisposed) return;
                RomanizationPacksStatus = string.Format(Strings.SettingsPage_RomanizationPacks_LoadFailedFormat, ex.Message);
            });
        }
        finally
        {
            _dispatcherService.TryEnqueue(() =>
            {
                if (!token.IsCancellationRequested && !_isDisposed) IsLoadingRomanizationPacks = false;
            });
        }
    }

    [RelayCommand]
    private async Task InstallRomanizationPackAsync(RomanizationPackViewModel? pack)
    {
        if (pack is null) return;

        pack.IsBusy = true;
        RomanizationPacksStatus = null;
        try
        {
            var result = await _romanizationPackManager.InstallPackAsync(pack.Id);
            if (!result.Succeeded)
            {
                RomanizationPacksStatus = Strings.SettingsPage_RomanizationPacks_InstallFailed;
                return;
            }

            await LoadRomanizationPacksAsync(forceRefresh: false);
        }
        finally
        {
            pack.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveRomanizationPackAsync(RomanizationPackViewModel? pack)
    {
        if (pack is null) return;

        pack.IsBusy = true;
        RomanizationPacksStatus = null;
        try
        {
            var result = await _romanizationPackManager.RemovePackAsync(pack.Id);
            if (!result.Succeeded)
            {
                RomanizationPacksStatus = Strings.SettingsPage_RomanizationPacks_RemoveFailed;
                return;
            }

            await LoadRomanizationPacksAsync(forceRefresh: false);
        }
        finally
        {
            pack.IsBusy = false;
        }
    }
}
