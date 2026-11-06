using System.Diagnostics.CodeAnalysis;

namespace Romd.Domain.Source.Rom;

/// <summary>A stable recipe identifier; validation never normalizes persisted values.</summary>
public sealed record RecipeId
{
    private RecipeId(string value) => Value = value;
    public string Value { get; }
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out RecipeId? recipe)
    {
        recipe = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
            return false;
        recipe = new(value);
        return true;
    }
    public static RecipeId Parse(string value) => TryParse(value, out var recipe) ? recipe
        : throw new ArgumentException("Invalid convergence recipe.", nameof(value));
    public override string ToString() => Value;
}
