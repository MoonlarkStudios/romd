namespace Romd.Application.Common.Content;

/// <summary>An immutable recipe's explicit reason to rebuild even when captured parameters match.</summary>
public sealed record MediaRecipeRebuild
{
    private MediaRecipeRebuild(string? reason) => Reason = reason;
    public static MediaRecipeRebuild MatchingParameters { get; } = new((string?)null);
    public string? Reason { get; }
    public static MediaRecipeRebuild Required(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512 || reason.Trim() != reason || reason.Any(char.IsControl))
            throw new ArgumentException("A deliberate recipe rebuild requires its bounded reason.", nameof(reason));
        return new(reason);
    }
}
