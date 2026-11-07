using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Settings;
using Nagi.Presentation.Helpers;

namespace Nagi.Presentation.ViewModels;

public sealed record SortOption(Enum Order, string Label);

/// <summary>
///     What a sort menu needs from a list, whatever the type of its sort orders.
/// </summary>
public interface ISortOrderSelection : INotifyPropertyChanged
{
    IReadOnlyList<SortOption> Options { get; }
    Enum Current { get; }
    string Text { get; }
    ICommand ChangeCommand { get; }
}

/// <summary>
///     The orders a list offers and the one it is sorted by. Choosing one saves it and re-sorts the list.
/// </summary>
public sealed partial class SortOrderSelection<TSort> : ObservableObject, ISortOrderSelection where TSort : struct, Enum
{
    private readonly Func<TSort, Task> _save;
    private readonly Func<Task> _apply;
    private readonly ILogger _logger;

    /// <param name="apply">Re-sorts the list after the order changed.</param>
    public SortOrderSelection(IReadOnlyList<TSort> options, TSort current, Func<TSort, Task> save, Func<Task> apply,
        ILogger logger)
    {
        Options = options.Select(order => new SortOption(order, SortOrderHelper.GetLabel(order))).ToList();
        Current = current;
        _save = save;
        _apply = apply;
        _logger = logger;
    }

    /// <summary>Starts from the order saved in <paramref name="setting" /> and saves each choice there.</summary>
    public SortOrderSelection(IReadOnlyList<TSort> options, Setting<TSort> setting, ISettingsService settings,
        Func<Task> apply, ILogger logger)
        : this(options, settings.Get(setting), order =>
        {
            settings.Set(setting, order);
            return Task.CompletedTask;
        }, apply, logger)
    {
    }

    public IReadOnlyList<SortOption> Options { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    public partial TSort Current { get; set; }

    Enum ISortOrderSelection.Current => Current;

    public string Text => SortOrderHelper.GetDisplayName(Current);

    ICommand ISortOrderSelection.ChangeCommand => ChangeCommand;

    [RelayCommand]
    private async Task ChangeAsync(TSort order)
    {
        if (EqualityComparer<TSort>.Default.Equals(order, Current)) return;
        Current = order;
        _logger.LogDebug("Sort order changed to '{SortOrder}'", order);
        try
        {
            await _save(order);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save sort order {SortOrder}", order);
        }

        await _apply();
    }
}
