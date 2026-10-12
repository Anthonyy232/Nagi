using System;
using System.ComponentModel;

namespace Nagi.WinUI.ViewModels;

/// <summary>
///     What a list needs to highlight its "now playing" row: which song is current and whether
///     playback is running. Raises <see cref="INotifyPropertyChanged.PropertyChanged" /> for both.
/// </summary>
public interface INowPlayingSource : INotifyPropertyChanged
{
    Guid? CurrentPlayingSongId { get; }
    bool IsPlaying { get; }
}
