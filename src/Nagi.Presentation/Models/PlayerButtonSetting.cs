using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Serialization;

namespace Nagi.Presentation.Models;

public partial class PlayerButtonSetting : ObservableObject
{
    [ObservableProperty] public partial bool IsEnabled { get; set; }

    public required string Id { get; set; }
    public required string DisplayName { get; set; }
    public AppIcon Icon { get; set; }

    [JsonIgnore]
    public bool IsSeparator => Id == "Separator";

    /// <summary>
    ///     XAML binding counterpart to <see cref="IsSeparator"/>.
    /// </summary>
    [JsonIgnore]
    public bool IsNotSeparator => !IsSeparator;

    // Dynamic properties for binding - ignored by JSON serializer as they are runtime state
    [JsonIgnore] public ICommand? Command { get; set; }

    [ObservableProperty]
    [property: JsonIgnore]
    public partial bool IsIconActive { get; set; } = true;

    [ObservableProperty]
    [property: JsonIgnore]
    public partial AppIcon DynamicIcon { get; set; }

    [ObservableProperty]
    [property: JsonIgnore]
    public partial string? DynamicToolTip { get; set; }

    public override string ToString() => DisplayName;
}
