using Romd.Application.Common.Ids;

namespace Romd.Host.Endpoints;

/// <summary>
///     Decodes the public ids a request body carries as strings
///     (docs/decisions/api-contract-policy.md, "Opaque Public Identity"); route and query ids bind
///     as <see cref="Sqid" /> instead. A malformed or non-canonical id answers a 400 envelope keyed
///     by its body field, so each method returns that problem, or <c>null</c> once every id decodes.
/// </summary>
internal static class RequestBodyIds
{
    public static IResult? Decode(string? value, string field, string errorCode, out int id) =>
        IdCoder.TryDecode(value, out id) ? null : Invalid(field, errorCode, [value]);

    /// <summary>An absent value is valid and decodes to <c>null</c>.</summary>
    public static IResult? DecodeOptional(string? value, string field, string errorCode, out int? id)
    {
        id = null;
        if (value is null)
            return null;

        var problem = Decode(value, field, errorCode, out int decoded);
        id = problem is null ? decoded : null;
        return problem;
    }

    /// <summary>Decodes every value in order, reporting each invalid one.</summary>
    public static IResult? DecodeAll(IEnumerable<string?> values, string field, string errorCode, out IReadOnlyList<int> ids)
    {
        var failures = new Dictionary<string, string[]>();
        ids = Collect(values, field, failures);
        return failures.Count == 0 ? null : ProblemResults.ValidationProblem(errorCode, failures);
    }

    /// <summary>
    ///     Decodes every value in order and records each invalid one under <paramref name="field" />
    ///     in <paramref name="failures" />, so a caller can report every failure of a body in one
    ///     envelope. The ids are meaningful only when nothing was recorded.
    /// </summary>
    public static IReadOnlyList<int> Collect(IEnumerable<string?> values, string field, IDictionary<string, string[]> failures)
    {
        var decoded = values
            .Select(value => (Value: value, Valid: IdCoder.TryDecode(value, out int id), Id: id))
            .ToArray();
        string[] invalid = decoded
            .Where(entry => !entry.Valid)
            .Select(entry => InvalidIdMessage(entry.Value))
            .ToArray();

        if (invalid.Length > 0)
            failures[field] = invalid;
        return decoded.Select(entry => entry.Id).ToArray();
    }

    /// <summary>
    ///     An absent value is valid and decodes to <c>null</c>; an invalid one is recorded under
    ///     <paramref name="field" /> in <paramref name="failures" />, and the id is then meaningless.
    /// </summary>
    public static int? CollectOptional(string? value, string field, IDictionary<string, string[]> failures) =>
        value is null ? null : Collect([value], field, failures)[0];

    private static IResult Invalid(string field, string errorCode, IEnumerable<string?> values) =>
        ProblemResults.ValidationProblem(
            errorCode,
            new Dictionary<string, string[]>
            {
                [field] = values.Select(InvalidIdMessage).ToArray()
            });

    private static string InvalidIdMessage(string? value) => $"'{value ?? "null"}' is not a valid ID.";
}
