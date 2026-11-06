using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.ReadModels;

/// <summary>One persisted title media row.</summary>
public sealed record TitleMediaSummary(
    int Id,
    MediaType Type,
    int FileId,
    string SourceId,
    string? SourceUrl,
    string? Attribution,
    string? SourcePageUrl,
    bool IsPrimary);
