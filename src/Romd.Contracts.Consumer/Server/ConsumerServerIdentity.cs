namespace Romd.Contracts.Consumer.Server;

public sealed record ConsumerServerIdentity
{
    public required string InstanceId { get; init; }
}
