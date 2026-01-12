using System;
using System.Collections.Generic;
using System.Linq;

namespace Nagi.Core.Models;

/// <summary>
///     Encapsulates a logical selection state that efficiently handles both 
///     explicit selections and "Select All" with exceptions.
/// </summary>
public class SelectionState
{
    private readonly HashSet<Guid> _exceptions = new();

    /// <summary>
    ///     Gets or sets a value indicating whether the "Select All" mode is active.
    ///     In this mode, all items are considered selected except those in the exceptions list.
    /// </summary>
    public bool IsSelectAllMode { get; private set; }

    /// <summary>
    ///     Gets the count of exceptions (unselected items in Select All mode, or selected items in normal mode).
    /// </summary>
    public int ExceptionCount => _exceptions.Count;

    /// <summary>
    ///     Checks if a specific ID is logically selected.
    /// </summary>
    public bool IsSelected(Guid id) => IsSelectAllMode ? !_exceptions.Contains(id) : _exceptions.Contains(id);

    /// <summary>
    ///     Explicitly selects a specific ID.
    /// </summary>
    public void Select(Guid id)
    {
        if (IsSelectAllMode)
            _exceptions.Remove(id);
        else
            _exceptions.Add(id);
    }

    /// <summary>
    ///     Explicitly deselects a specific ID.
    /// </summary>
    public void Deselect(Guid id)
    {
        if (IsSelectAllMode)
            _exceptions.Add(id);
        else
            _exceptions.Remove(id);
    }

    /// <summary>
    ///     Activates "Select All" mode and clears any exceptions.
    /// </summary>
    public void SelectAll()
    {
        IsSelectAllMode = true;
        _exceptions.Clear();
    }

    /// <summary>
    ///     Deactivates "Select All" mode and clears all selections.
    /// </summary>
    public void Clear() => DeselectAll();

    /// <summary>
    ///     Deactivates "Select All" mode and clears all selections.
    /// </summary>
    public void DeselectAll()
    {
        IsSelectAllMode = false;
        _exceptions.Clear();
    }

    /// <summary>
    ///     Calculates the total number of selected items based on the total items in the view.
    /// </summary>
    public int GetSelectedCount(int totalItems) => 
        IsSelectAllMode ? Math.Max(0, totalItems - _exceptions.Count) : _exceptions.Count;

    /// <summary>
    ///     Gets the set of IDs that are explicitly handled as exceptions (unselected in Select All, or selected in Normal mode).
    /// </summary>
    public IEnumerable<Guid> GetExplicitlySelectedIds() => _exceptions.ToList();

    /// <summary>
    ///     Materializes the sequence of selected IDs based on a provided master list of IDs.
    /// </summary>
    public IEnumerable<Guid> GetSelectedIds(IEnumerable<Guid> fullIdList)
    {
        if (fullIdList == null) return Enumerable.Empty<Guid>();
        
        return IsSelectAllMode 
            ? fullIdList.Where(id => !_exceptions.Contains(id))
            : fullIdList.Where(id => _exceptions.Contains(id));
    }
}
