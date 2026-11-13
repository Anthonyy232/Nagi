namespace Nagi.Presentation.Lists;

public sealed record SongInfoField(string Label, string Value);

public sealed record SongInfoSection(string Title, IReadOnlyList<SongInfoField> Fields);
