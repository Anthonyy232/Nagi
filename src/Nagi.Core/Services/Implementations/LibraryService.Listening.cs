using Microsoft.EntityFrameworkCore;
using Nagi.Core.Data;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

public partial class LibraryService
{
    public async Task<bool> SetSongsLovedAsync(IEnumerable<Guid> songIds, bool loved)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false);
        var updated = 0;
        foreach (var ids in songIds.Distinct().Chunk(500))
            updated += await context.Songs.Where(s => ids.Contains(s.Id) && s.IsLoved != loved)
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.IsLoved, loved)).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        if (updated > 0) LibraryContentChanged?.Invoke(this, new(LibraryChangeType.SongsUpdated));
        return updated > 0;
    }
}
