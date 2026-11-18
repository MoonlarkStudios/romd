using System.ComponentModel.DataAnnotations;

namespace Romd.Contracts.Management.Models;

public sealed record SignInMessageSettings(Guid Revision, string Message);

public sealed record ReplaceSignInMessageRequest
{
    public const int MaximumLength = 500;

    [MaxLength(MaximumLength)]
    public required string Message { get; init; }
}
