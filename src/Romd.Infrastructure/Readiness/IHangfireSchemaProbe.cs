namespace Romd.Infrastructure.Readiness;

public interface IHangfireSchemaProbe
{
    Task<int?> GetPublishedVersionAsync(CancellationToken cancellationToken);
}
