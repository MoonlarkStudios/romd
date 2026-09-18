namespace Romd.Contracts.Management.Libraries;

public sealed record UpdateLibraryRequest(
    string Name,
    LibraryConfigurationDto? Configuration = null,
    bool? IsDefault = null);
