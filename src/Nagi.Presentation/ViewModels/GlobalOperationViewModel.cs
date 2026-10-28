using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Core.Services.Data;

namespace Nagi.Presentation.ViewModels;

/// <summary>
///     The app-wide status bar for long-running library work such as adding, rescanning or removing
///     folders and metadata or ReplayGain scans: whether one is running, its progress, and how to cancel it.
/// </summary>
public partial class GlobalOperationViewModel : ObservableObject
{
    private Action? _cancel;

    [ObservableProperty] public partial bool IsInProgress { get; set; }
    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial double ProgressValue { get; set; }
    [ObservableProperty] public partial bool IsIndeterminate { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsCancellable { get; set; }

    /// <summary>
    ///     Offers <paramref name="cancel" /> through <see cref="CancelCommand" />, or withdraws the offer when null.
    /// </summary>
    public void SetCancellation(Action? cancel)
    {
        Interlocked.Exchange(ref _cancel, cancel);
        IsCancellable = cancel != null;
    }

    /// <summary>Shows a scan's progress here until <paramref name="token" /> is cancelled.</summary>
    public IProgress<ScanProgress> CreateProgress(CancellationToken token = default) =>
        new Progress<ScanProgress>(progress =>
        {
            if (token.IsCancellationRequested) return;
            StatusMessage = progress.StatusText;
            IsIndeterminate = progress.IsIndeterminate || progress.Percentage < 5;
            ProgressValue = progress.Percentage;
        });

    private bool CanCancel() => IsCancellable;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        var cancel = Interlocked.Exchange(ref _cancel, null);
        IsCancellable = false;
        cancel?.Invoke();
    }
}
