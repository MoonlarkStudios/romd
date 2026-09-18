using System.Linq.Expressions;

namespace Romd.Persistence;

/// <summary>
///     Compresses dense integer identity sets into range predicates before EF translates them.
///     High-fan-out catalog mutations commonly touch contiguous identities; representing those
///     as two range bounds avoids thousands of SQL parameters and their managed allocation.
///     Sparse sets fall back to the provider's ordinary collection translation.
/// </summary>
public static class IntegerKeyPredicate
{
    private const int MaxCompressedRanges = 128;

    public static Expression<Func<TEntity, bool>> Create<TEntity>(
        IReadOnlyCollection<int> ids,
        Expression<Func<TEntity, int>> keySelector)
    {
        ArgumentOutOfRangeException.ThrowIfZero(ids.Count);
        var ordered = ids.Distinct().Order().ToArray();
        var ranges = new List<(int Start, int End)>();
        int start = ordered[0];
        int end = start;
        for (int index = 1; index < ordered.Length; index++)
        {
            int value = ordered[index];
            if (end != int.MaxValue && value == end + 1)
            {
                end = value;
                continue;
            }

            ranges.Add((start, end));
            start = end = value;
        }
        ranges.Add((start, end));

        if (ranges.Count > MaxCompressedRanges)
        {
            var contains = Expression.Call(
                typeof(Enumerable),
                nameof(Enumerable.Contains),
                [typeof(int)],
                Expression.Constant(ordered),
                keySelector.Body);
            return Expression.Lambda<Func<TEntity, bool>>(contains, keySelector.Parameters);
        }

        Expression? body = null;
        foreach ((int rangeStart, int rangeEnd) in ranges)
        {
            Expression range = rangeStart == rangeEnd
                ? Expression.Equal(keySelector.Body, Expression.Constant(rangeStart))
                : Expression.AndAlso(
                    Expression.GreaterThanOrEqual(keySelector.Body, Expression.Constant(rangeStart)),
                    Expression.LessThanOrEqual(keySelector.Body, Expression.Constant(rangeEnd)));
            body = body is null ? range : Expression.OrElse(body, range);
        }

        return Expression.Lambda<Func<TEntity, bool>>(body!, keySelector.Parameters);
    }
}
