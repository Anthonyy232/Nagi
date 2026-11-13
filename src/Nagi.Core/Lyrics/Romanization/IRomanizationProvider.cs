namespace Nagi.Core.Lyrics.Romanization;

/// <summary>Romanizes one script with the rules a romanization pack supplies.</summary>
public interface IRomanizationProvider
{
    string EngineId { get; }

    /// <summary>The rules file in the data folder of the provider's packs.</summary>
    string RulesFileName { get; }

    bool Supports(string text);

    /// <summary>Reads the rules file.</summary>
    /// <returns>The rules, to pass to <see cref="Romanize" />.</returns>
    object ParseRules(string json);

    /// <summary>Romanizes text this provider <see cref="Supports" />.</summary>
    string Romanize(string text, object rules);
}
