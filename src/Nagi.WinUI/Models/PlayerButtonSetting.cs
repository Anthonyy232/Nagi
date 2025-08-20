using CommunityToolkit.Mvvm.ComponentModel;

namespace Nagi.WinUI.Models;

/// <summary>
///     Represents a customizable button on the player control bar.
/// </summary>
public partial class PlayerButtonSetting : ObservableObject
{
    [ObservableProperty] public partial bool IsEnabled { get; set; }

    public required string Id { get; set; }
    public required string DisplayName { get; set; }
    public required string IconGlyph { get; set; }

    /// <summary>
    ///     Gets a value indicating whether this item represents the layout separator.
    /// </summary>
    public bool IsSeparator => Id == "Separator";

    /// <summary>
    ///     Gets a value indicating whether this item is a regular button, not the separator.
    ///     This is a convenience property for XAML bindings which do not easily support negation.
    /// </summary>
    public bool IsNotSeparator => Id != "Separator";
}