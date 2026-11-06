namespace Romd.Contracts.Consumer.Server;

public sealed record ConsumerServerIdentityDto
{
    public required string InstanceId { get; init; }
}
