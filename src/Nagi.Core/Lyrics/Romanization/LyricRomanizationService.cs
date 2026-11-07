using System.Collections.Concurrent;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Settings;

namespace Nagi.Core.Lyrics.Romanization;

public sealed class LyricRomanizationService : ILyricRomanizationService
{
    private readonly IFileSystemService _fileSystem;
    private readonly IRomanizationPackManager _packManager;
    private readonly IReadOnlyList<IRomanizationProvider> _providers;
    private readonly ISettingsService _settingsService;

    // An installed pack's files don't change without its version changing.
    private readonly ConcurrentDictionary<(string PackId, string Version), object> _rules = new();

    public LyricRomanizationService(
        ISettingsService settingsService,
        IRomanizationPackManager packManager,
        IEnumerable<IRomanizationProvider> providers,
        IFileSystemService fileSystem)
    {
        _settingsService = settingsService;
        _packManager = packManager;
        _providers = providers.ToList();
        _fileSystem = fileSystem;
    }

    public async Task<IReadOnlyList<LyricLine>> ApplyRomanizationAsync(IEnumerable<LyricLine> lines, CancellationToken cancellationToken = default)
    {
        var snapshot = lines
            .Select(line => new LyricLine(line.StartTime, line.Text) { TranslatedText = line.TranslatedText })
            .ToList();

        if (!_settingsService.Get(CoreSettings.LyricsRomanization))
            return snapshot;

        var installedPacks = await _packManager.GetInstalledPacksAsync(cancellationToken).ConfigureAwait(false);
        var packsByEngine = installedPacks
            .DistinctBy(pack => pack.Manifest.EngineId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pack => pack.Manifest.EngineId, StringComparer.OrdinalIgnoreCase);

        var romanizedByText = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var line in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line.Text)) continue;

            if (!romanizedByText.TryGetValue(line.Text, out var romanized))
            {
                romanized = await RomanizeLineAsync(line.Text, packsByEngine).ConfigureAwait(false);
                romanizedByText[line.Text] = romanized;
            }

            line.RomanizedText = romanized;
        }

        return snapshot;
    }

    /// <returns>The romanized text, or null when no installed pack changes it.</returns>
    private async Task<string?> RomanizeLineAsync(string text, Dictionary<string, InstalledRomanizationPack> packsByEngine)
    {
        var romanized = text;

        // Each provider romanizes its own script and leaves the rest for the next one.
        foreach (var provider in _providers)
        {
            if (!provider.Supports(romanized)) continue;
            if (!packsByEngine.TryGetValue(provider.EngineId, out var pack)) continue;

            var result = provider.Romanize(romanized, await GetRulesAsync(provider, pack).ConfigureAwait(false));
            if (!string.IsNullOrWhiteSpace(result)) romanized = result;
        }

        return string.Equals(romanized, text, StringComparison.Ordinal) ? null : romanized;
    }

    private async Task<object> GetRulesAsync(IRomanizationProvider provider, InstalledRomanizationPack pack)
    {
        var key = (pack.Manifest.Id, pack.Manifest.Version);
        if (_rules.TryGetValue(key, out var rules)) return rules;

        rules = await RomanizationPackFiles.LoadRulesAsync(_fileSystem, provider, pack).ConfigureAwait(false);
        _rules[key] = rules;
        return rules;
    }
}
