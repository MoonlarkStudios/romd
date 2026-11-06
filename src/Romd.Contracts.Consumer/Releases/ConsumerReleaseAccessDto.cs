using System.Text.Json.Serialization;

namespace Romd.Contracts.Consumer.Releases;

public sealed record ConsumerReleaseAccessDto
{
    public required string ServerInstanceId { get; init; }
    public required string ReleaseId { get; init; }
    public required bool Allowed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TitleId { get; init; }
}
