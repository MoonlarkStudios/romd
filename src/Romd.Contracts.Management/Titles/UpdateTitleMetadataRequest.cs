using System.Text.Json;
using System.Text.Json.Serialization;
using Romd.Contracts.Common.ReferenceCatalog;

namespace Romd.Contracts.Management.Titles;

/// <summary>
///     A partial update of the user-authored metadata of a title. An absent field is unchanged; an explicit null
///     removes the user override for that field, which falls back to provider data; a value sets the override.
///     Empty strings clear the field entirely.
/// </summary>
[JsonConverter(typeof(ReferencePatchJsonConverter))]
public sealed class UpdateTitleMetadataRequest : ReferencePatchDto
{
    /// <summary>Every editable field clears its user override with null, so the set follows the properties.</summary>
    private static readonly HashSet<string> EveryFieldClears = ReferencePatchJsonConverter
        .EditableProperties(typeof(UpdateTitleMetadataRequest))
        .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
        .ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> NoResetOnlyFields = [];

    private string? _publisher;
    private string? _developer;
    private string? _genre;
    private DateOnly? _releaseDate;
    private int? _players;
    private double? _rating;

    public override IReadOnlySet<string> ClearableFields => EveryFieldClears;
    public override IReadOnlySet<string> ResetOnlyFields => NoResetOnlyFields;

    public string? Publisher { get => _publisher; set { _publisher = value; Set("publisher", value); } }
    public string? Developer { get => _developer; set { _developer = value; Set("developer", value); } }
    public string? Genre { get => _genre; set { _genre = value; Set("genre", value); } }
    public DateOnly? ReleaseDate { get => _releaseDate; set { _releaseDate = value; Set("releaseDate", value); } }
    public int? Players { get => _players; set { _players = value; Set("players", value); } }
    public double? Rating { get => _rating; set { _rating = value; Set("rating", value); } }
}
