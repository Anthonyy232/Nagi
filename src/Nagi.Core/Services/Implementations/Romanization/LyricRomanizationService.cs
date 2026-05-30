using Nagi.Core.Models.Lyrics;
using Nagi.Core.Models.Romanization;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations.Romanization;

public sealed class LyricRomanizationService : ILyricRomanizationService
{
    private readonly IRomanizationPackManager _packManager;
    private readonly IReadOnlyList<IRomanizationProvider> _providers;
    private readonly ISettingsService _settingsService;

    public LyricRomanizationService(
        ISettingsService settingsService,
        IRomanizationPackManager packManager,
        IEnumerable<IRomanizationProvider> providers)
    {
        _settingsService = settingsService;
        _packManager = packManager;
        _providers = providers.ToList();
    }

    public Task<IReadOnlyList<LyricLine>> ApplyRomanizationAsync(IEnumerable<string> lines, CancellationToken cancellationToken = default)
    {
        return ApplyRomanizationAsync(lines.Select(line => new LyricLine(TimeSpan.Zero, line)), cancellationToken);
    }

    public async Task<IReadOnlyList<LyricLine>> ApplyRomanizationAsync(IEnumerable<LyricLine> lines, CancellationToken cancellationToken = default)
    {
        var snapshot = lines
            .Select(line => new LyricLine(line.StartTime, line.Text))
            .ToList();

        if (!await _settingsService.GetLyricsRomanizationEnabledAsync().ConfigureAwait(false))
            return snapshot;

        var installedPacks = await _packManager.GetInstalledPacksAsync(cancellationToken).ConfigureAwait(false);
        var packsByEngine = installedPacks
            .GroupBy(pack => pack.Manifest.EngineId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var cache = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var line in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line.Text)) continue;

            if (!cache.TryGetValue(line.Text, out var romanized))
            {
                romanized = await RomanizeLineAsync(line.Text, packsByEngine, cancellationToken).ConfigureAwait(false);
                cache[line.Text] = romanized;
            }

            if (!string.IsNullOrWhiteSpace(romanized)
                && !string.Equals(romanized, line.Text, StringComparison.Ordinal))
            {
                line.RomanizedText = romanized;
            }
        }

        return snapshot;
    }

    private async Task<string?> RomanizeLineAsync(
        string text,
        Dictionary<string, InstalledRomanizationPack> packsByEngine,
        CancellationToken cancellationToken)
    {
        var romanized = text;
        var changed = false;

        foreach (var provider in _providers)
        {
            if (!provider.Supports(romanized)) continue;
            if (!packsByEngine.TryGetValue(provider.EngineId, out var pack)) continue;

            var providerResult = await provider.RomanizeAsync(romanized, pack, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(providerResult)
                || string.Equals(providerResult, romanized, StringComparison.Ordinal))
            {
                continue;
            }

            romanized = providerResult;
            changed = true;
        }

        return changed ? romanized : null;
    }
}
