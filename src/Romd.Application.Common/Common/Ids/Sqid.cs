using System.Diagnostics.CodeAnalysis;

namespace Romd.Application.Common.Ids;

/// <summary>
///     A strongly-typed wrapper for decoded Sqid identifiers.
///     Implements IParsable for automatic ASP.NET Core model binding.
/// </summary>
public readonly record struct Sqid(int Value) : IParsable<Sqid>
{
    /// <summary>
    ///     Implicit conversion to int for seamless use with repository methods.
    /// </summary>
    public static implicit operator int(Sqid sqid) => sqid.Value;

    /// <summary>
    ///     Returns the encoded friendly ID string.
    /// </summary>
    public override string ToString() => IdCoder.Encode(Value);

    /// <summary>
    ///     Parses a friendly ID string into a Sqid.
    ///     Throws FormatException if the string is invalid.
    /// </summary>
    public static Sqid Parse(string s, IFormatProvider? provider)
    {
        if (!TryParse(s, provider, out var result))
        {
            throw new FormatException($"'{s}' is not a valid Sqid.");
        }

        return result;
    }

    /// <summary>
    ///     Attempts to parse a friendly ID string into a Sqid.
    /// </summary>
    public static bool TryParse(
        [NotNullWhen(true)] string? s,
        IFormatProvider? provider,
        out Sqid result)
    {
        if (IdCoder.TryDecode(s, out int decoded))
        {
            result = new Sqid(decoded);
            return true;
        }

        result = default;
        return false;
    }
}
