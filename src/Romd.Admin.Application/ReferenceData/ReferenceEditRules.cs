using ErrorOr;

namespace Romd.Admin.Application.ReferenceData;

internal static class ReferenceEditRules
{
    internal static Error? CheckVersion(string current, string? expected) => string.IsNullOrWhiteSpace(expected)
        ? ReferenceEditErrors.PreconditionRequired()
        : expected.Split(',').Select(x => x.Trim()).Contains(current, StringComparer.Ordinal) ? null
        : ReferenceEditErrors.PreconditionFailed();
    internal static bool Label(string? value, int max) => value is { Length: > 0 } && value.Length <= max && value.Trim() == value && !value.Any(char.IsControl);
    internal static bool Key(string? value) => value is { Length: > 6 and <= 50 } && value.StartsWith("local-", StringComparison.Ordinal)
        && System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z0-9]+(?:-[a-z0-9]+)*$");
    internal static bool Asset(string? value) => value is null || System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-f0-9]{64}$");
}
