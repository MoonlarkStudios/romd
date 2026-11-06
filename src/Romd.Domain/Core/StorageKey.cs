using System.Diagnostics.CodeAnalysis;

namespace Romd.Domain.Core;

/// <summary>
///     A validated storage key (relative path) for addressing files in storage.
///     Guarantees the key is safe: no path traversal, no absolute paths, no drive letters.
/// </summary>
/// <remarks>
///     Warning: As a struct, default(StorageKey) is always constructible but represents
///     an invalid state. Access to Value on a default instance will throw.
///     Use IsDefault to check, or prefer nullable StorageKey? when "no value" is valid.
/// </remarks>
public readonly record struct StorageKey
{
    private readonly string? _value;

    private StorageKey(string value) => _value = value;

    /// <summary>
    ///     Returns true if this is a default (uninitialized) instance.
    /// </summary>
    public bool IsDefault => _value is null;

    /// <summary>
    ///     Gets the validated storage key string.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when accessing Value on a default instance.</exception>
    public string Value => _value ?? throw new InvalidOperationException(
        "Cannot access Value on a default StorageKey instance. Use IsDefault to check or ensure proper initialization via Parse/TryParse.");

    /// <summary>
    ///     Parses a string into a StorageKey, validating it is safe for storage operations.
    /// </summary>
    /// <exception cref="FormatException">Thrown when the string is not a valid storage key.</exception>
    public static StorageKey Parse(string value)
    {
        if (!TryParse(value, out var result, out var error))
        {
            throw new FormatException(error);
        }

        return result;
    }

    /// <summary>
    ///     Attempts to parse a string into a StorageKey.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out StorageKey result)
    {
        return TryParse(value, out result, out _);
    }

    /// <summary>
    ///     Attempts to parse a string into a StorageKey, providing an error message on failure.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out StorageKey result, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = default;
            error = "Storage key cannot be null or empty.";
            return false;
        }

        // Reject keys with path traversal patterns
        if (value.Contains(".."))
        {
            result = default;
            error = "Storage key cannot contain '..'.";
            return false;
        }

        // Reject rooted/absolute paths
        if (Path.IsPathRooted(value))
        {
            result = default;
            error = "Storage key cannot be an absolute path.";
            return false;
        }

        // Reject Windows drive letters (e.g., "C:")
        if (value.Length >= 2 && value[1] == ':')
        {
            result = default;
            error = "Storage key cannot contain drive letters.";
            return false;
        }

        // Normalize path separators to forward slashes for consistency
        string normalized = value.Replace('\\', '/');

        result = new StorageKey(normalized);
        error = null;
        return true;
    }

    /// <summary>
    ///     Implicit conversion to string for compatibility.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when converting a default instance.</exception>
    public static implicit operator string(StorageKey key) => key.Value;

    public override string ToString() => _value ?? "<default>";
}
