namespace Romd.Infrastructure.Storage;

/// <summary>Worker composition must validate this before accepting file work.</summary>
public sealed class MediaWorkerStartupValidator(IMediaWorkerSupervisor supervisor,
    MediaWorkerSupervisor? chdSupervisor = null, ChdDeploymentRuntime? deployment = null)
{
    public async Task<MediaWorkerResult> ValidateAsync(CancellationToken ct = default)
    {
        var result = await supervisor.HandshakeAsync(ct);
        if (!result.IsSuccess || deployment?.Assets is null)
            return result;
        if (!deployment.IsReady || chdSupervisor is null)
            return MediaWorkerResult.Failed(MediaWorkerFailure.Startup);
        return await chdSupervisor.HandshakeChdAsync(ct);
    }
}
