namespace Nagi.Core.Services.Data;

public sealed record GenreSummary(Guid Id, string Name, int SongCount)
{
    public override string ToString() => Name;
}
