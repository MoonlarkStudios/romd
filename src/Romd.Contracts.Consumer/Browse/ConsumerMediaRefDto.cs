namespace Romd.Contracts.Consumer.Browse;

public sealed record ConsumerMediaRefDto
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required string Url { get; init; }
    public required bool IsPrimary { get; init; }
}
