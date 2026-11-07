using Nagi.Core.Models.Lyrics;

namespace Nagi.Core.Services.Abstractions;

public interface ILyricRomanizationService
{
    Task<IReadOnlyList<LyricLine>> ApplyRomanizationAsync(IEnumerable<LyricLine> lines, CancellationToken cancellationToken = default);
}
