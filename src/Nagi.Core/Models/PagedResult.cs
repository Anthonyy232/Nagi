namespace Nagi.Core.Models;

/// <summary>
/// Represents a single page of results from a query, supporting incremental loading.
/// </summary>
/// <typeparam name="T">The type of the items in the page.</typeparam>
public class PagedResult<T> {
    /// <summary>
    /// The collection of items for the current page.
    /// </summary>
    public IEnumerable<T> Items { get; init; } = Enumerable.Empty<T>();

    /// <summary>
    /// The total number of items available across all pages for the query.
    /// </summary>
    public int TotalCount { get; init; }

    /// <summary>
    /// The 1-based index of the current page.
    /// </summary>
    public int PageNumber { get; init; }

    /// <summary>
    /// The maximum number of items per page.
    /// </summary>
    public int PageSize { get; init; }

    /// <summary>
    /// Calculates the total number of pages available.
    /// </summary>
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling(TotalCount / (double)PageSize) : 0;

    /// <summary>
    /// Determines if there is a subsequent page of items.
    /// </summary>
    public bool HasNextPage => PageNumber < TotalPages;

    /// <summary>
    /// Determines if there is a preceding page of items.
    /// </summary>
    public bool HasPreviousPage => PageNumber > 1;
}