namespace Nagi.Core.Library;

public sealed record GenreSummary(Guid Id, string Name, int SongCount)
{
    public override string ToString() => Name;
}
