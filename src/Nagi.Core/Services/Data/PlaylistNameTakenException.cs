namespace Nagi.Core.Services.Data;

/// <summary>
///     Thrown when creating or renaming a playlist would give it the name of another playlist of the same kind.
/// </summary>
public sealed class PlaylistNameTakenException(string name)
    : InvalidOperationException($"A playlist named '{name}' already exists.");
