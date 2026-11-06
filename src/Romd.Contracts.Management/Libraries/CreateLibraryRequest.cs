namespace Romd.Contracts.Management.Libraries;

public sealed record CreateLibraryRequest(
    string Name,
    LibraryConfigurationDto? Configuration = null,
    bool IsDefault = false);
