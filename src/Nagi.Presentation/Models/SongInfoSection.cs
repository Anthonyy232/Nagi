using System.Collections.Generic;

namespace Nagi.Presentation.Models;

public sealed record SongInfoField(string Label, string Value);

public sealed record SongInfoSection(string Title, IReadOnlyList<SongInfoField> Fields);
