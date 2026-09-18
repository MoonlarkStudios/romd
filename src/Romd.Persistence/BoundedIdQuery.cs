namespace Romd.Persistence;

/// <summary>
///     Shared ceiling for client-resolved id collections used in provider-neutral LINQ queries.
///     Keeping each collection far below provider parameter limits also bounds command construction and
///     remains portable to providers that translate <c>Contains</c> differently.
/// </summary>
public static class BoundedIdQuery
{
    public const int MaxIdsPerBatch = 500;

    /// <summary>
    ///     Returns distinct ids in first-seen order, split into arrays that are safe to use in a
    ///     LINQ <c>Contains</c> predicate.
    /// </summary>
    public static IEnumerable<int[]> DistinctBatches(IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var seen = new HashSet<int>();
        var batch = new List<int>(MaxIdsPerBatch);

        foreach (int id in ids)
        {
            if (!seen.Add(id))
            {
                continue;
            }

            batch.Add(id);
            if (batch.Count == MaxIdsPerBatch)
            {
                yield return batch.ToArray();
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            yield return batch.ToArray();
        }
    }
}
