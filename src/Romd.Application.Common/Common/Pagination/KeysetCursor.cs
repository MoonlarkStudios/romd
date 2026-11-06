using System.Security.Cryptography;
using System.Text.Json;
using ErrorOr;

namespace Romd.Application.Common.Pagination;

/// <summary>A bounded opaque position bound to normalized filters, visibility and stable sort semantics.</summary>
public static class KeysetCursor
{
    public const int MaxLength = 1024;
    public static string Fingerprint(params object?[] filters) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(filters)).AsSpan(0, 16));
    public static string Encode<TPosition>(TPosition position, string fingerprint) where TPosition : class =>
        CursorUtils.ToCursor(new Envelope<TPosition>(position, fingerprint));
    public static ErrorOr<TPosition?> Decode<TPosition>(string? cursor, string fingerprint, Func<TPosition, bool> valid,
        int maxLength = MaxLength)
        where TPosition : class
    {
        if (cursor is null)
            return ErrorOrFactory.From<TPosition?>((TPosition?)null);
        if (!CursorUtils.TryReadCanonical<Envelope<TPosition>>(cursor, maxLength, out var envelope)
            || envelope.Fingerprint != fingerprint || envelope.Position is null || !valid(envelope.Position))
            return Error.Validation("Pagination.InvalidCursor", "The cursor is invalid for this query. Start from the first page.");
        return envelope.Position;
    }
    private sealed record Envelope<TPosition>(TPosition Position, string Fingerprint);
}

public sealed record DateIdPosition(DateTimeOffset CreatedAt, Guid Id)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => CreatedAt != default && Id != Guid.Empty;
}
public sealed record IntegerPosition(int Id)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Id > 0;
}
