namespace Romd.Contracts.Management.Libraries;

public sealed record CreateLibraryRequest(
    string Name,
    LibraryConfiguration? Configuration = null,
    bool IsDefault = false);
