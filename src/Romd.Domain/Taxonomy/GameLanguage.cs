namespace Romd.Domain.Taxonomy;

/// <summary>
///     Represents a canonical language for game releases.
///     Languages are resolved from raw DAT file tokens via an alias lookup system.
/// </summary>
public sealed class GameLanguage : ITaxonomyEntity
{
    private GameLanguage(
        int id,
        string name,
        string code,
        int sortOrder,
        bool isAutoCreated)
    {
        Id = id;
        Name = name;
        Code = code;
        SortOrder = sortOrder;
        IsAutoCreated = isAutoCreated;
    }

    public int Id { get; private set; }

    /// <summary>
    ///     Full display name of the language.
    ///     Example: "English", "Japanese", "French"
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    ///     ISO 639-1 language code.
    ///     Example: "en", "ja", "fr"
    /// </summary>
    public string Code { get; private set; }

    /// <summary>
    ///     Determines default display ordering (lower = higher priority).
    /// </summary>
    public int SortOrder { get; private set; }

    /// <summary>
    ///     True if this language was automatically created from an unrecognized DAT token.
    /// </summary>
    public bool IsAutoCreated { get; private set; }

    /// <summary>
    ///     Creates a new language with validated invariants.
    /// </summary>
    public static GameLanguage CreateNew(
        string name,
        string code,
        int sortOrder = 0,
        bool isAutoCreated = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        return new GameLanguage(0, name, code, sortOrder, isAutoCreated);
    }

    /// <summary>
    ///     Rehydrates a language from persistence. Trusts that data is valid.
    /// </summary>
    internal static GameLanguage Rehydrate(
        int id,
        string name,
        string code,
        int sortOrder,
        bool isAutoCreated) =>
        new(id, name, code, sortOrder, isAutoCreated);
}
