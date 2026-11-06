using ErrorOr;
using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Storage.Files;

/// <summary>All byte ceilings needed by one attempt, reserved before materialization.</summary>
public sealed record TempFileAggregateRequest(Guid JobId, string Operation, long SourceBytes,
    long OutputMaximumBytes, long VerificationMaximumBytes = 0);

public interface IAggregateTempFileFactory
{
    Task<ErrorOr<ITempFileAggregate>> ReserveAggregateAsync(TempFileAggregateRequest request, CancellationToken ct = default);
}

/// <summary>Owns an all-or-none reservation. Verification may stream without a separate payload.</summary>
public interface ITempFileAggregate : IAsyncDisposable
{
    Guid JobId { get; }
    IWritableTempFile? Output { get; }
    IWritableTempFile? Verification { get; }
    Task<ErrorOr<ITempFile>> MaterializeSourceAsync(Stream input, Sha256 expectedSha256, long expectedBytes,
        CancellationToken ct = default);
}
