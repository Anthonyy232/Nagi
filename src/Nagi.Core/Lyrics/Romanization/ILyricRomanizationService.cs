namespace Nagi.Core.Lyrics.Romanization;

public interface ILyricRomanizationService
{
    Task<IReadOnlyList<LyricLine>> ApplyRomanizationAsync(IEnumerable<LyricLine> lines, CancellationToken cancellationToken = default);
}
