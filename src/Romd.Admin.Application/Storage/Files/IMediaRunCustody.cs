using ErrorOr;

namespace Romd.Admin.Application.Storage.Files;

/// <summary>Authoritative runtime check before a fenced terminal mutation releases durable source ownership.</summary>
public interface IMediaRunCustody
{
    Task<ErrorOr<Success>> EnsureJobStoppedAsync(Guid jobId, CancellationToken ct = default);
}
