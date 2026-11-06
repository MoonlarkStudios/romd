namespace Romd.Infrastructure.Storage;

internal sealed record MediaStagingBinding(string PoolRoot, int Slot, string Path, long ReservedBytes,
    Guid? JobId, string? AttemptOperation, string Role);

internal interface IMediaStagingBinding
{
    MediaStagingBinding StagingBinding { get; }
}

internal interface IMediaStagingAggregate
{
    IReadOnlyList<MediaStagingBinding> StagingBindings { get; }
}
