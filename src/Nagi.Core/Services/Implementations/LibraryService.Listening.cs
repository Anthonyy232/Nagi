using Microsoft.EntityFrameworkCore;
using Nagi.Core.Data;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

public partial class LibraryService
{
    public async Task<IReadOnlyList<MixCandidate>> GetMixCandidatesAsync(IEnumerable<Guid> songIds, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var result = new List<MixCandidate>();
        foreach (var ids in songIds.Distinct().Chunk(500))
            result.AddRange(await context.Songs.AsNoTracking().Where(s => ids.Contains(s.Id))
                .Select(s => new MixCandidate(s.Id, s.PrimaryArtistName, s.DurationTicks, s.PlayCount, s.LastPlayedDate))
                .ToListAsync(token).ConfigureAwait(false));
        return result;
    }
}
