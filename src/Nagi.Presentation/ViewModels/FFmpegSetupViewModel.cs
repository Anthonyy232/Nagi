using CommunityToolkit.Mvvm.ComponentModel;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.ViewModels;

public enum FFmpegSetupStatus
{
    NotDetected,
    Checking,
    Detected,
    StillNotDetected
}

/// <summary>
///     The FFmpeg setup dialog, which stays open until a recheck finds FFmpeg or the user cancels.
/// </summary>
public partial class FFmpegSetupViewModel(Func<Task<bool>> isInstalled) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial FFmpegSetupStatus Status { get; private set; }

    public string StatusText => Status switch
    {
        FFmpegSetupStatus.Checking => Strings.FFmpeg_Status_Checking,
        FFmpegSetupStatus.Detected => Strings.FFmpeg_Status_Detected,
        FFmpegSetupStatus.StillNotDetected => Strings.FFmpeg_Status_StillNotDetected,
        _ => Strings.FFmpeg_Status_NotDetected
    };

    /// <returns>Whether FFmpeg is installed, so the dialog can close.</returns>
    public async Task<bool> RecheckAsync()
    {
        Status = FFmpegSetupStatus.Checking;
        Status = await isInstalled() ? FFmpegSetupStatus.Detected : FFmpegSetupStatus.StillNotDetected;
        return Status == FFmpegSetupStatus.Detected;
    }
}
