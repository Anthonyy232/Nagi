using System.ComponentModel;
using Nagi.Presentation.Common;

namespace Nagi.Presentation.Models;

/// <summary>
///     An item of a <see cref="ViewModels.ReorderableToggleList{T}" />.
/// </summary>
public interface IToggleItem : INotifyPropertyChanged
{
    bool IsEnabled { get; set; }

    string DisplayName { get; }

    AppIcon Icon { get; }

    string? Description => null;
}
