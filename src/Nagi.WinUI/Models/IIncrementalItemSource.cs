using System.Threading.Tasks;

namespace Nagi.WinUI.Models;

/// <summary>
///     A collection that loads its items a page at a time as the list showing it scrolls.
/// </summary>
public interface IIncrementalItemSource
{
    bool HasMoreItems { get; }

    /// <summary>
    ///     Loads the next page. Concurrent calls share the load in progress.
    /// </summary>
    /// <returns>The number of items added.</returns>
    Task<int> LoadMoreItemsAsync();
}
