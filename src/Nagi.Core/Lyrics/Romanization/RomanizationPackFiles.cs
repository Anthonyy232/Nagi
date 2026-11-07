using System.Text.Json;
using Nagi.Core.Platform;

namespace Nagi.Core.Lyrics.Romanization;

internal static class RomanizationPackFiles
{
    public static async Task<object> LoadRulesAsync(IFileSystemService fileSystem, IRomanizationProvider provider,
        InstalledRomanizationPack pack) =>
        provider.ParseRules(await fileSystem.ReadAllTextAsync(RulesPath(provider, pack)).ConfigureAwait(false));

    /// <summary>Checks that the pack holds the provider's rules and that they reproduce the pack's own test cases.</summary>
    public static async Task<bool> ValidateAsync(IFileSystemService fileSystem, IRomanizationProvider provider,
        InstalledRomanizationPack pack, CancellationToken cancellationToken)
    {
        if (!fileSystem.FileExists(RulesPath(provider, pack))) return false;
        var rules = await LoadRulesAsync(fileSystem, provider, pack).ConfigureAwait(false);

        var goldenPath = Path.Combine(pack.DirectoryPath, "tests", "golden.json");
        if (!fileSystem.FileExists(goldenPath)) return true;

        var cases = JsonSerializer.Deserialize<List<RomanizationGoldenCase>>(
            await fileSystem.ReadAllTextAsync(goldenPath).ConfigureAwait(false), RomanizationJson.Options) ?? [];
        foreach (var testCase in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actual = provider.Supports(testCase.Input) ? provider.Romanize(testCase.Input, rules) : testCase.Input;
            if (!string.Equals(actual, testCase.Expected, StringComparison.Ordinal)) return false;
        }

        return true;
    }

    private static string RulesPath(IRomanizationProvider provider, InstalledRomanizationPack pack) =>
        Path.Combine(pack.DirectoryPath, "data", provider.RulesFileName);
}
