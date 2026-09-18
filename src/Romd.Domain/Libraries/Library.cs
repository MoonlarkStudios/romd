namespace Romd.Domain.Libraries;

/// <summary>
///     A materialized library that pre-computes which Titles and DatGame releases
///     are accessible using access configuration and content filtering.
///     Aggregate root managing materialization lifecycle.
/// </summary>
public sealed class Library
{
    private Library(
        int id,
        string name,
        LibraryConfiguration configuration,
        LibraryConfigurationState configurationState,
        string? configurationError,
        bool isDefault,
        bool needsMaterialization,
        DateTimeOffset? lastMaterializedAt,
        int itemCount,
        DateTimeOffset createdAt,
        DateTimeOffset? updatedAt)
    {
        Id = id;
        Name = name;
        Configuration = configuration;
        ConfigurationState = configurationState;
        ConfigurationError = configurationError;
        IsDefault = isDefault;
        NeedsMaterialization = needsMaterialization;
        LastMaterializedAt = lastMaterializedAt;
        ItemCount = itemCount;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public int Id { get; private set; }
    public string Name { get; private set; }
    public LibraryConfiguration Configuration { get; private set; }
    public LibraryConfigurationState ConfigurationState { get; private set; }
    public string? ConfigurationError { get; private set; }
    public bool HasValidConfiguration => ConfigurationState == LibraryConfigurationState.Valid;
    public bool IsDefault { get; private set; }
    public bool NeedsMaterialization { get; private set; }
    public long MaterializationGeneration { get; private set; }
    public Guid MaterializationRevision { get; private set; } = Guid.NewGuid();
    public DateTimeOffset? LastMaterializedAt { get; private set; }
    public int ItemCount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>
    ///     Creates a new library with validated invariants. Flagged for materialization immediately.
    /// </summary>
    public static Library CreateNew(string name, LibraryConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configuration);

        return new Library(
            id: 0,
            name: name.Trim(),
            configuration: configuration,
            configurationState: LibraryConfigurationState.Valid,
            configurationError: null,
            isDefault: false,
            needsMaterialization: true,
            lastMaterializedAt: null,
            itemCount: 0,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: null);
    }

    /// <summary>
    ///     Rehydrates a library from persistence. Trusts that data is valid.
    /// </summary>
    internal static Library Rehydrate(
        int id,
        string name,
        LibraryConfiguration configuration,
        LibraryConfigurationState configurationState,
        string? configurationError,
        bool isDefault,
        bool needsMaterialization,
        DateTimeOffset? lastMaterializedAt,
        int itemCount,
        DateTimeOffset createdAt,
        DateTimeOffset? updatedAt) =>
        RehydrateWithMaterializationGeneration(
            id,
            name,
            configuration,
            configurationState,
            configurationError,
            isDefault,
            needsMaterialization,
            lastMaterializedAt,
            itemCount,
            createdAt,
            updatedAt,
            materializationGeneration: 0);

    internal static Library RehydrateWithMaterializationGeneration(
        int id,
        string name,
        LibraryConfiguration configuration,
        LibraryConfigurationState configurationState,
        string? configurationError,
        bool isDefault,
        bool needsMaterialization,
        DateTimeOffset? lastMaterializedAt,
        int itemCount,
        DateTimeOffset createdAt,
        DateTimeOffset? updatedAt,
        long materializationGeneration,
        Guid? materializationRevision = null)
    {
        var library = new Library(
            id,
            name,
            configuration,
            configurationState,
            configurationError,
            isDefault,
            needsMaterialization,
            lastMaterializedAt,
            itemCount,
            createdAt,
            updatedAt);
        library.MaterializationGeneration = materializationGeneration;
        library.MaterializationRevision = materializationRevision ?? library.MaterializationRevision;
        return library;
    }

    /// <summary>
    ///     Updates the library name and configuration. Flags for rematerialization.
    /// </summary>
    public void UpdateConfiguration(string name, LibraryConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configuration);

        Name = name.Trim();
        Configuration = configuration;
        ConfigurationState = LibraryConfigurationState.Valid;
        ConfigurationError = null;
        NeedsMaterialization = true;
        UpdatedAt = DateTimeOffset.UtcNow;
        MaterializationRevision = Guid.NewGuid();
    }

    public void MarkConfigurationInvalid(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        ConfigurationState = LibraryConfigurationState.Invalid;
        ConfigurationError = error;
        NeedsMaterialization = false;
        ItemCount = 0;
        UpdatedAt = DateTimeOffset.UtcNow;
        MaterializationRevision = Guid.NewGuid();
    }

    public void MarkAsDefault()
    {
        IsDefault = true;
        UpdatedAt = DateTimeOffset.UtcNow;
        MaterializationRevision = Guid.NewGuid();
    }

    public void UnmarkAsDefault()
    {
        IsDefault = false;
        UpdatedAt = DateTimeOffset.UtcNow;
        MaterializationRevision = Guid.NewGuid();
    }

    /// <summary>
    ///     Records that materialization completed successfully.
    /// </summary>
    public void MarkMaterialized(int itemCount)
    {
        NeedsMaterialization = false;
        LastMaterializedAt = DateTimeOffset.UtcNow;
        ItemCount = itemCount;
    }

    /// <summary>
    ///     Flags this library for rematerialization (e.g., after ROM upload, DAT change, enrichment).
    /// </summary>
    public void FlagForRematerialization()
    {
        NeedsMaterialization = true;
        UpdatedAt = DateTimeOffset.UtcNow;
        MaterializationRevision = Guid.NewGuid();
    }
}
