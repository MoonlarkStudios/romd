namespace Romd.Admin.Application.Ingestion.Classification;

/// <summary>
///     Types of files that can be uploaded.
/// </summary>
public enum FileType
{
    Unknown,
    Dat,
    Rom,
    Archive
}

/// <summary>
///     Result of classifying an uploaded file.
/// </summary>
public sealed record FileClassification(
    FileType Type,
    string? DetectedFormat,
    float Confidence);
