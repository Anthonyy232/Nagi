using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using FluentAssertions;
using Jeffijoe.MessageFormat;
using Nagi.Presentation.Helpers;
using Xunit;
using CoreStrings = Nagi.Core.Resources.Strings;
using UiStrings = Nagi.Presentation.Resources.Strings;

namespace Nagi.Presentation.Tests;

public class LocalizedResourceTests
{
    [Fact]
    public void Formatter_UsesCurrentUiCultureForCachedPatternsAndSupportsNetSpecifiers()
    {
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            const string pattern = "{0, plural, one {# item} other {# items}}";
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
            ResourceFormatter.Format(pattern, 21).Should().Be("21 item");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");
            ResourceFormatter.Format(pattern, 21).Should().Be("21 items");

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            ResourceFormatter.Format("{0:N0}", 1234).Should().Be("1,234");
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUiCulture;
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Translations_KeepTheSourcePlaceholdersAndLineBreaks()
    {
        var problems = new List<string>();
        foreach (var manager in new[] { CoreStrings.ResourceManager, UiStrings.ResourceManager })
        {
            var source = ReadStrings(manager, CultureInfo.InvariantCulture);
            var satellite = manager.BaseName.Split('.')[1] == "Core" ? "Nagi.Core.resources.dll" : "Nagi.Presentation.resources.dll";
            var cultures = Directory.GetDirectories(AppContext.BaseDirectory)
                .Where(dir => File.Exists(Path.Combine(dir, satellite)))
                .Select(dir => CultureInfo.GetCultureInfo(Path.GetFileName(dir)))
                .ToList();
            cultures.Should().NotBeEmpty();

            foreach (var culture in cultures)
            foreach (var (key, text) in ReadStrings(manager, culture))
            {
                var where = $"{culture.Name} {key}";
                if (!source.TryGetValue(key, out var original))
                {
                    problems.Add($"{where}: key is not in the source");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(text))
                    problems.Add($"{where}: empty translation");
                if (text.Count(c => c == '\n') != original.Count(c => c == '\n'))
                    problems.Add($"{where}: line breaks differ from the source");

                // A translation may switch a plain count to an ICU plural, but it must still use the
                // same arguments, and plain .NET placeholders must keep their exact format.
                if (IsIcu(text))
                {
                    if (!ArgumentIndexes(text).SetEquals(ArgumentIndexes(original)))
                        problems.Add($"{where}: ICU pattern uses different arguments than the source");
                    if (!TryFormatIcu(text, ArgumentIndexes(original).Count))
                        problems.Add($"{where}: ICU pattern does not format");
                }
                else if (!NetPlaceholders(text).SetEquals(NetPlaceholders(original)))
                {
                    problems.Add($"{where}: placeholders differ from the source ({original} -> {text})");
                }
                else if (!TryFormatNet(text, ArgumentIndexes(original).Count))
                {
                    problems.Add($"{where}: string does not format");
                }
            }
        }

        problems.Should().BeEmpty();
    }

    private static Dictionary<string, string> ReadStrings(ResourceManager manager, CultureInfo culture) =>
        manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!);

    private static bool IsIcu(string text) => Regex.IsMatch(text, @"\{\s*\d+\s*,\s*(plural|select|selectordinal)\s*,");

    private static HashSet<string> ArgumentIndexes(string text) =>
        Regex.Matches(text, @"(?<!\{)\{\s*(\d+)\s*[,:}]").Select(m => m.Groups[1].Value).ToHashSet();

    private static HashSet<string> NetPlaceholders(string text) =>
        Regex.Matches(text, @"(?<!\{)\{\d+(,-?\d+)?(:[^{}]*)?\}").Select(m => m.Value).ToHashSet();

    private static object[] SampleArguments(int count) => Enumerable.Range(0, count).Select(i => (object)(i + 2)).ToArray();

    private static bool TryFormatNet(string text, int argumentCount)
    {
        try
        {
            _ = string.Format(CultureInfo.InvariantCulture, text, SampleArguments(argumentCount));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryFormatIcu(string text, int argumentCount)
    {
        try
        {
            foreach (var count in new[] { 0, 1, 2, 5, 21 })
            {
                var args = Enumerable.Range(0, argumentCount)
                    .ToDictionary(i => i.ToString(CultureInfo.InvariantCulture), _ => (object?)count);
                _ = MessageFormatter.Format(text, args);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [Fact]
    public void GeneratedAccessors_FollowUiCultureAndFallBackToNeutralResources()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            CoreStrings.Label_Title.Should().Be("Titel");
            UiStrings.Label_Composer.Should().Be("Komponist");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-AU");
            CoreStrings.Label_Title.Should().Be("Title");
            UiStrings.Label_Composer.Should().Be("Composer");
            UiStrings.Label_Grouping.Should().Be("Grouping");
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }
}
