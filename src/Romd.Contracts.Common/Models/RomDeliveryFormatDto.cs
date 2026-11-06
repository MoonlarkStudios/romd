using System.Text.Json.Serialization;

namespace Romd.Contracts.Common.Models;

/// <summary>
///     Format metadata describes delivered bytes. Empty applicability makes no runtime assertion. The label and file
///     extension come from the server's format catalog, so clients display them instead of keeping format tables.
/// </summary>
public abstract record RomDeliveryFormatMetadataDto
{
    public required string Format { get; init; }
    /// <summary>The format's display label, for example <c>RVZ</c>.</summary>
    public required string Label { get; init; }
    /// <summary>The extension that ends an encoded format's filename; omitted for <c>original</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileExtension { get; init; }
    public required string State { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Filename { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ByteCount? SizeBytes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Md5 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha1 { get; init; }
    public required string ProfileVersion { get; init; }
    public required IReadOnlyList<string> RuntimeApplicability { get; init; }
}

public sealed record RomDeliveryFormatDto : RomDeliveryFormatMetadataDto;
