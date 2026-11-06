using System.Reflection;
using System.Text.RegularExpressions;
using Romd.Host.Endpoints;

namespace Romd.Hosting;

/// <summary>
///     Describes a request-binding failure for the shared envelope
///     (docs/decisions/api-contract-policy.md, "Strict Validation" and "One Error Envelope"): a
///     route, query or header parameter failure is <c>Request.InvalidParameter</c> with the
///     parameter's wire name in <c>errors</c>, 413 and 415 keep their own codes, and every other
///     binding failure is a body failure. The framework reports parameter failures only through the
///     <see cref="BadHttpRequestException" /> message, so the formats matched here are pinned by
///     tests. Messages never echo the rejected value.
/// </summary>
internal static partial class BindingFailureProblem
{
    private const string InvalidValueMessage = "The value is not valid.";
    private const string MissingValueMessage = "A value is required.";

    public static (string ErrorCode, IReadOnlyDictionary<string, string[]>? Errors) Describe(
        BadHttpRequestException exception,
        Endpoint? endpoint) =>
        exception.StatusCode switch
        {
            StatusCodes.Status413PayloadTooLarge => (ProblemCodes.RequestPayloadTooLarge, null),
            StatusCodes.Status415UnsupportedMediaType => (ProblemCodes.RequestUnsupportedMediaType, null),
            _ when InvalidValue().Match(exception.Message) is { Success: true } invalid =>
                Parameter(invalid.Groups["name"].Value, endpoint, InvalidValueMessage),
            _ when MissingValue().Match(exception.Message) is { Success: true } missing =>
                Parameter(missing.Groups["name"].Value, endpoint, MissingValueMessage),
            _ => (ProblemCodes.RequestInvalidBody, null)
        };

    private static (string, IReadOnlyDictionary<string, string[]>?) Parameter(
        string parameterName,
        Endpoint? endpoint,
        string message) =>
        (ProblemCodes.RequestInvalidParameter,
            new Dictionary<string, string[]> { [WireName(parameterName, endpoint)] = [message] });

    /// <summary>The framework names the handler parameter, which may bind under another wire name.</summary>
    private static string WireName(string parameterName, Endpoint? endpoint) =>
        endpoint?.Metadata.GetMetadata<MethodInfo>()?.GetParameters()
            .FirstOrDefault(parameter => parameter.Name == parameterName) is { } parameter
            ? ParameterWireName.Of(parameter)
            : parameterName;

    /// <summary><c>Failed to bind parameter "{type} {name}" from "{value}".</c>: a parameter that failed to parse.</summary>
    [GeneratedRegex(
        @"^Failed to bind parameter ""(?<type>.+) (?<name>[^"" ]+)"" from """,
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex InvalidValue();

    /// <summary><c>Required parameter "{type} {name}" was not provided from {source}.</c> for a non-body source.</summary>
    [GeneratedRegex(
        @"^Required parameter ""(?<type>.+) (?<name>[^"" ]+)"" was not provided from (?:route|query string|route or query string|header)\.$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex MissingValue();
}
