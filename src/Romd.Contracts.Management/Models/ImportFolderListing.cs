namespace Romd.Contracts.Management.Models;

/// <summary>
///     One level of the server folders a path import may read. Without a path it lists the
///     configured allowed roots; with one, the folders directly inside it. Symbolic links and
///     hidden folders are never listed, and nothing outside the allowed roots is reachable.
/// </summary>
public sealed record ImportFolderListing
{
    /// <summary>Whether the operator configured at least one allowed import root.</summary>
    public required bool Enabled { get; init; }

    /// <summary>The listed folder, or null for the list of allowed roots.</summary>
    public string? Path { get; init; }

    /// <summary>The folder above <see cref="Path" />, or null when <see cref="Path" /> is an allowed root.</summary>
    public string? ParentPath { get; init; }

    public required IReadOnlyList<ImportFolder> Folders { get; init; }

    /// <summary>Regular files directly inside <see cref="Path" />, not counting subfolders.</summary>
    public required int FileCount { get; init; }

    /// <summary>Whether more folders exist than were listed.</summary>
    public required bool Truncated { get; init; }
}

/// <summary>A folder a path import may start from.</summary>
public sealed record ImportFolder
{
    public required string Name { get; init; }

    /// <summary>Absolute canonical path to pass back as the listing path or the import path.</summary>
    public required string Path { get; init; }
}
