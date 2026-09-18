namespace Romd.Domain.Source.Platform;

/// <summary>
///     Represents a hardware platform/console in the system.
///     Platforms are the top-level organizational unit for titles and DAT files.
/// </summary>
public sealed class Platform
{
    private Platform(
        int id,
        string name,
        string shortName,
        string? manufacturer,
        DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        ShortName = shortName;
        Manufacturer = manufacturer;
        CreatedAt = createdAt;
    }

    public int Id { get; private set; }

    /// <summary>
    ///     Full display name of the platform.
    ///     Example: "Super Nintendo Entertainment System"
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    ///     Short identifier for the platform, used in paths and URLs.
    ///     Example: "snes"
    /// </summary>
    public string ShortName { get; private set; }

    /// <summary>
    ///     The company that manufactured this platform.
    ///     Example: "Nintendo"
    /// </summary>
    public string? Manufacturer { get; private set; }

    /// <summary>
    ///     When this platform was added to the database.
    /// </summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    ///     Creates a new platform with validated invariants.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when name or shortName is null or whitespace.</exception>
    public static Platform CreateNew(
        string name,
        string shortName,
        string? manufacturer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(shortName);

        return new Platform(
            0,
            name,
            shortName,
            manufacturer,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    ///     Rehydrates a platform from persistence. Trusts that data is valid.
    /// </summary>
    internal static Platform Rehydrate(
        int id,
        string name,
        string shortName,
        string? manufacturer,
        DateTimeOffset createdAt) =>
        new(id, name, shortName, manufacturer, createdAt);
}
