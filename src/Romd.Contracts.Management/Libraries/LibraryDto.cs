using Romd.Contracts.Management.Export;

namespace Romd.Contracts.Management.Libraries;

public sealed record LibraryDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Attached collection IDs, populated by the library list query.</summary>
    public IReadOnlyList<string>? CollectionIds { get; init; }
    public LibraryConfigurationDto? Configuration { get; init; }
    public required string ConfigurationState { get; init; }
    public string? ConfigurationError { get; init; }
    public required bool IsDefault { get; init; }
    public required bool NeedsMaterialization { get; init; }
    public DateTimeOffset? LastMaterializedAt { get; init; }
    public required int ItemCount { get; init; }

    /// <summary>
    ///     The formats this library's export can be requested in: original, then the stored format of each of its systems.
    ///     An export binds a stored format where a file has it and falls back to the original otherwise.
    /// </summary>
    public required IReadOnlyList<ArchiveFormatDto> ExportFormats { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}
