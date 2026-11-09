namespace Romd.Contracts.Management.Libraries;

public sealed record UpdateLibraryRequest(
    string Name,
    LibraryConfiguration? Configuration = null,
    bool? IsDefault = null);
