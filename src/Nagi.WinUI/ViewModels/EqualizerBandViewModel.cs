using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

public partial class EqualizerBandViewModel : ObservableObject, IDisposable
{
    private const int KiloHertzThreshold = 1000;

    private readonly Debouncer _debouncer;
    private readonly IDispatcherService _dispatcherService;
    private bool _isExternalUpdate;

    public EqualizerBandViewModel(uint index, float frequency, float initialGain, IDispatcherService dispatcherService, TimeProvider? timeProvider = null)
    {
        _dispatcherService = dispatcherService;
        _debouncer = new Debouncer(TimeSpan.FromMilliseconds(AudioSettingsViewModel.EqualizerDebounceDelayMilliseconds), timeProvider);
        Index = index;
        FrequencyLabel = frequency < KiloHertzThreshold ? $"{frequency:F0}" : $"{frequency / KiloHertzThreshold:F0}K";
        SetGainFromService(initialGain);
    }

    public uint Index { get; }
    public string FrequencyLabel { get; }

    [ObservableProperty] public partial float Gain { get; set; }

    public event Action<EqualizerBandViewModel>? GainChanged;
    public event Action<EqualizerBandViewModel, float>? GainCommitted;

    public void SetGainFromService(float gain)
    {
        _isExternalUpdate = true;
        try { Gain = gain; }
        finally { _isExternalUpdate = false; }
    }

    internal Task PendingGainCommit => _debouncer.PendingInvocation;

    public void CancelPendingGain() => _debouncer.Cancel();

    public void Dispose() => _debouncer.Dispose();

    partial void OnGainChanged(float value)
    {
        if (_isExternalUpdate) return;

        GainChanged?.Invoke(this);
        _debouncer.Trigger(token => _dispatcherService.EnqueueAsync(() =>
        {
            if (!token.IsCancellationRequested) GainCommitted?.Invoke(this, value);
            return Task.CompletedTask;
        }));
    }
}
