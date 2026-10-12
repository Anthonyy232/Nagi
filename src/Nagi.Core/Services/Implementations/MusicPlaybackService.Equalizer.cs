using Nagi.Core.Models;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Equalizer bands, presets and preamp.
/// </summary>
public partial class MusicPlaybackService
{
    public Task SetEqualizerBandAsync(uint bandIndex, float gain)
    {
        var settings = CurrentEqualizerSettings;
        if (settings == null || bandIndex >= settings.BandGains.Count) return Task.CompletedTask;
        var gains = new List<float>(settings.BandGains);
        gains[(int)bandIndex] = gain;
        return ApplyEqualizerAsync(new EqualizerSettings { Preamp = settings.Preamp, BandGains = gains });
    }

    public Task SetEqualizerGainsAsync(IEnumerable<float> gains)
    {
        var settings = CurrentEqualizerSettings;
        if (settings == null) return Task.CompletedTask;
        var updatedGains = gains.Take(settings.BandGains.Count).ToList();
        updatedGains.AddRange(settings.BandGains.Skip(updatedGains.Count));
        return ApplyEqualizerAsync(new EqualizerSettings { Preamp = settings.Preamp, BandGains = updatedGains });
    }

    public EqualizerPreset? GetMatchingPreset(IEnumerable<float>? bandGains)
    {
        if (bandGains == null) return null;
        var currentGains = bandGains.ToArray();

        return AvailablePresets.FirstOrDefault(preset =>
            preset.Gains.Length == currentGains.Length &&
            preset.Gains.Zip(currentGains).All(pair => Math.Abs(pair.First - pair.Second) <= 0.1f));
    }

    public Task SetEqualizerPreampAsync(float gain)
    {
        var settings = CurrentEqualizerSettings;
        return settings == null ? Task.CompletedTask : ApplyEqualizerAsync(new EqualizerSettings
        {
            Preamp = gain,
            BandGains = new List<float>(settings.BandGains)
        });
    }

    public Task ResetEqualizerAsync() => CurrentEqualizerSettings == null
        ? Task.CompletedTask
        : ApplyEqualizerAsync(new EqualizerSettings
        {
            BandGains = Enumerable.Repeat(0.0f, CurrentEqualizerSettings.BandGains.Count).ToList()
        });

    private async Task ApplyEqualizerAsync(EqualizerSettings settings)
    {
        CurrentEqualizerSettings = settings;
        _audioPlayer.ApplyEqualizerSettings(settings);
        await _settingsService.SetEqualizerSettingsAsync(settings);
        EqualizerChanged?.Invoke();
    }
}
