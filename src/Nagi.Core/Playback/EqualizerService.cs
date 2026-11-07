using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Settings;

namespace Nagi.Core.Playback;

public sealed class EqualizerService(IAudioPlayer audioPlayer, ISettingsService settingsService,
    ILogger<EqualizerService> logger) : IEqualizerService
{
    private Task? _initialization;

    public IReadOnlyList<(uint Index, float Frequency)> Bands { get; private set; } = [];
    public EqualizerSettings? CurrentSettings { get; private set; }

    public IReadOnlyList<EqualizerPreset> Presets { get; } =
    [
        new(Resources.Strings.EqPreset_None, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        new(Resources.Strings.EqPreset_Classical, [0, 0, 0, 0, 0, 0, -3.2f, -3.2f, -3.2f, -4.0f]),
        new(Resources.Strings.EqPreset_Dance, [3.2f, 2.4f, 0.8f, 0, 0, -1.6f, -2.4f, -2.4f, 0, 0]),
        new(Resources.Strings.EqPreset_FullBass, [3.2f, 3.2f, 3.2f, 1.6f, 0, -1.6f, -3.2f, -3.2f, -3.2f, -4.0f]),
        new(Resources.Strings.EqPreset_FullTreble, [-3.2f, -3.2f, -3.2f, -1.6f, 0, 1.6f, 3.2f, 3.2f, 3.2f, 4.0f]),
        new(Resources.Strings.EqPreset_Jazz, [1.6f, 0.8f, 0, 0.8f, -0.8f, -0.8f, -1.6f, 0, 0.8f, 1.6f]),
        new(Resources.Strings.EqPreset_Pop, [-1.6f, 1.6f, 2.4f, 2.4f, 1.6f, 0, -0.8f, -0.8f, -1.6f, -1.6f]),
        new(Resources.Strings.EqPreset_Rock, [3.2f, 2.4f, -1.6f, -2.4f, -0.8f, 1.6f, 2.4f, 3.2f, 3.2f, 3.2f]),
        new(Resources.Strings.EqPreset_Soft, [1.6f, 0.8f, 0, -0.8f, -0.8f, 0, 0.8f, 1.6f, 2.4f, 3.2f]),
        new(Resources.Strings.EqPreset_Techno, [3.2f, 2.4f, 0, -1.6f, -1.6f, 0, 1.6f, 2.4f, 2.4f, 3.2f]),
        new(Resources.Strings.EqPreset_Vocal, [-0.8f, -2.4f, -2.4f, 0, 1.6f, 1.6f, 1.6f, 0.8f, 0, -0.8f])
    ];

    public event Action? SettingsChanged;

    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            var bands = await audioPlayer.GetEqualizerBandsAsync();
            var settings = settingsService.Get(CoreSettings.Equalizer);
            if (settings != null && settings.BandGains.Count != bands.Count)
            {
                logger.LogWarning("Equalizer settings band count ({SavedCount}) does not match available bands ({AvailableCount}). Resetting equalizer to default.",
                    settings.BandGains.Count, bands.Count);
                settings = null;
            }

            if (settings == null)
            {
                settings = Flat(bands.Count);
                settingsService.Set(CoreSettings.Equalizer, settings);
            }

            Bands = bands;
            CurrentSettings = settings;
            await audioPlayer.ApplyEqualizerSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The equalizer is unavailable because the audio player failed to start.");
        }
    }

    public Task SetBandGainAsync(uint bandIndex, float gain)
    {
        var settings = CurrentSettings;
        if (settings == null || bandIndex >= settings.BandGains.Count) return Task.CompletedTask;
        var gains = new List<float>(settings.BandGains);
        gains[(int)bandIndex] = gain;
        return ApplyAsync(new EqualizerSettings { Preamp = settings.Preamp, BandGains = gains });
    }

    public Task SetBandGainsAsync(IEnumerable<float> gains)
    {
        var settings = CurrentSettings;
        if (settings == null) return Task.CompletedTask;
        var updatedGains = gains.Take(settings.BandGains.Count).ToList();
        updatedGains.AddRange(settings.BandGains.Skip(updatedGains.Count));
        return ApplyAsync(new EqualizerSettings { Preamp = settings.Preamp, BandGains = updatedGains });
    }

    public Task SetPreampAsync(float gain)
    {
        var settings = CurrentSettings;
        return settings == null
            ? Task.CompletedTask
            : ApplyAsync(new EqualizerSettings { Preamp = gain, BandGains = new List<float>(settings.BandGains) });
    }

    public Task ResetAsync() => CurrentSettings == null
        ? Task.CompletedTask
        : ApplyAsync(Flat(CurrentSettings.BandGains.Count));

    public EqualizerPreset? GetMatchingPreset(IEnumerable<float> bandGains)
    {
        var currentGains = bandGains.ToArray();
        return Presets.FirstOrDefault(preset =>
            preset.Gains.Length == currentGains.Length &&
            preset.Gains.Zip(currentGains).All(pair => Math.Abs(pair.First - pair.Second) <= 0.1f));
    }

    private static EqualizerSettings Flat(int bandCount) =>
        new() { BandGains = Enumerable.Repeat(0.0f, bandCount).ToList() };

    private async Task ApplyAsync(EqualizerSettings settings)
    {
        CurrentSettings = settings;
        settingsService.Set(CoreSettings.Equalizer, settings);
        SettingsChanged?.Invoke();
        await audioPlayer.ApplyEqualizerSettingsAsync(settings);
    }
}
