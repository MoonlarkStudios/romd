using ErrorOr;

namespace Romd.Host.Endpoints;

public static class ErrorOrResultExtensions
{
    private const string FallbackErrorCode = "General.Unexpected";

    public static IResult ToProblem(this IReadOnlyList<Error> errors)
    {
        if (errors.Count == 0)
        {
            return ProblemResults.Problem(
                StatusCodes.Status500InternalServerError,
                FallbackErrorCode,
                "An unexpected error occurred.");
        }

        var first = errors[0];

        return ProblemResults.Problem(
            ToStatusCode(first.Type),
            string.IsNullOrWhiteSpace(first.Code) ? FallbackErrorCode : first.Code,
            first.Description,
            CollectValidationFailures(errors));
    }

    // ErrorOr validation errors carry a stable code, not a field name, so the envelope's
    // errors map is keyed by error code here. Endpoint-level validation with real field
    // attribution uses ProblemResults.ValidationProblem with field keys instead
    // (docs/decisions/admin-api-contract-policy.md, One Error Envelope).
    private static Dictionary<string, string[]>? CollectValidationFailures(IReadOnlyList<Error> errors)
    {
        var failures = errors
            .Where(error => error.Type == ErrorType.Validation)
            .GroupBy(error => error.Code)
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.Description).ToArray());

        return failures.Count > 0 ? failures : null;
    }

    private static int ToStatusCode(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError
    };
}
