namespace Romd.Domain.Catalog;

/// <summary>
/// Immutable stored image identity. ContentVersion includes content identity and
/// transformation version; delivery URLs are constructed outside the domain.
/// </summary>
public sealed record ArtworkVariant
{
    public ArtworkVariant(int fileId, string contentVersion, string contentType, int width, int height, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateIdentity(contentVersion, 100, nameof(contentVersion));
        ValidateIdentity(name, 30, nameof(name));
        FileId = fileId;
        ContentVersion = contentVersion;
        ContentType = contentType;
        Width = width;
        Height = height;
        Name = name;
    }

    private static void ValidateIdentity(string value, int maxLength, string parameterName)
    {
        // Identities become URL path segments and must round-trip through both hosts.
        if (value.Length > maxLength || value is "." or ".." ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("Artwork identities must be URL-safe ASCII path segments.", parameterName);
    }

    public int FileId { get; }
    public string ContentVersion { get; }
    public string ContentType { get; }
    public int Width { get; }
    public int Height { get; }
    public string Name { get; }
}
