namespace Nagi.Core.Services.Data;

public static class Paging
{
    /// <summary>
    ///     The largest page a paged query returns. A list that shows every item at once loads pages of this size.
    /// </summary>
    public const int MaxPageSize = 500;

    internal static void Sanitize(ref int pageNumber, ref int pageSize)
    {
        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
    }
}
