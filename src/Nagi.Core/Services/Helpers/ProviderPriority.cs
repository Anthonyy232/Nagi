using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Helpers;

internal static class ProviderPriority
{
    /// <summary>
    ///     Awaits provider answers in priority order and hands each one with data to <paramref name="take" /> until it
    ///     reports that it has all it needs. Answers are requested as the sequence is enumerated: a list of lookups
    ///     already started runs them in parallel while a higher-priority provider still wins, and a lazy sequence asks
    ///     one provider at a time.
    /// </summary>
    /// <returns>
    ///     Whether the outcome is final: <paramref name="take" /> got all it needs, or every provider answered, so data
    ///     still missing is not worth asking for again.
    /// </returns>
    public static async Task<bool> TakeInOrderAsync<T>(IEnumerable<Task<ServiceResult<T>>> answers, Func<T, bool> take,
        CancellationToken cancellationToken)
        where T : class
    {
        var allAnswered = true;
        foreach (var answer in answers)
        {
            var result = await answer.WaitAsync(cancellationToken).ConfigureAwait(false);
            allAnswered &= result.IsConclusive;
            if (result.Data is not null && take(result.Data)) return true;
        }

        return allAnswered;
    }
}
