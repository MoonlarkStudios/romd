using ErrorOr;

namespace Romd.Application.Common.Validation;

/// <summary>
///     Validation errors attributed to the request input they belong to. The input's wire name travels in the
///     error's <see cref="MetadataKey" /> metadata entry, and the HTTP envelope keys the failure by it
///     (docs/decisions/api-contract-policy.md, "Strict Validation").
/// </summary>
public static class FieldErrors
{
    /// <summary>The metadata entry holding the wire name of the input a validation error belongs to.</summary>
    public const string MetadataKey = "field";

    public static Error Validation(string code, string description, string field) =>
        Error.Validation(code, description, new Dictionary<string, object> { [MetadataKey] = field });

    /// <summary>The wire name the error names, or null when it names none.</summary>
    public static string? FieldOf(Error error) => error.Metadata?.GetValueOrDefault(MetadataKey) as string;
}
