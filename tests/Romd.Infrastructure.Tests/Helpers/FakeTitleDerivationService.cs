using System.Runtime.CompilerServices;
using ErrorOr;
using Romd.Admin.Application.Catalog;

namespace Romd.Infrastructure.Tests.Helpers;

/// <summary>
///     Hand-written <see cref="ITitleDerivationService" /> for unit tests: records the claims
///     each stream delivered and yields one configurable assignment per claim, in claim order.
///     NSubstitute cannot mock IAsyncEnumerable-returning members (see the FakeMetadataProvider
///     precedent in Enrichment/Helpers).
/// </summary>
public sealed class FakeTitleDerivationService(
    Func<TitleClaim, int, ErrorOr<ClaimAssignment>>? assign = null) : ITitleDerivationService
{
    private readonly Func<TitleClaim, int, ErrorOr<ClaimAssignment>> _assign = assign ?? DefaultAssign;

    public List<(int CatalogSourceId, TitleClaim Claim)> ReconcileClaims { get; } = [];
    public List<(int CatalogSourceId, TitleClaim Claim)> UpsertClaims { get; } = [];

    public IAsyncEnumerable<ErrorOr<ClaimAssignment>> ReconcileAsync(
        int catalogSourceId,
        IAsyncEnumerable<TitleClaim> claims,
        CancellationToken cancellationToken = default) =>
        DeriveAsync(ReconcileClaims, catalogSourceId, claims, cancellationToken);

    public IAsyncEnumerable<ErrorOr<ClaimAssignment>> UpsertAsync(
        int catalogSourceId,
        IAsyncEnumerable<TitleClaim> claims,
        CancellationToken cancellationToken = default) =>
        DeriveAsync(UpsertClaims, catalogSourceId, claims, cancellationToken);

    private async IAsyncEnumerable<ErrorOr<ClaimAssignment>> DeriveAsync(
        List<(int CatalogSourceId, TitleClaim Claim)> log,
        int catalogSourceId,
        IAsyncEnumerable<TitleClaim> claims,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int index = 0;
        await foreach (var claim in claims.WithCancellation(cancellationToken))
        {
            log.Add((catalogSourceId, claim));
            yield return _assign(claim, index++);
        }
    }

    /// <summary>
    ///     Default contract-shaped answer: sequential entry ids in claim order; BIOS and
    ///     unrouted claims get entry identity but no title; linkable claims report an
    ///     existing (not created) title.
    /// </summary>
    private static ErrorOr<ClaimAssignment> DefaultAssign(TitleClaim claim, int index) =>
        new ClaimAssignment(
            claim.EntryKey,
            SourceEntryId: index + 1,
            TitleId: claim.IsBios || claim.PlatformId is null ? null : 1000 + index,
            TitleWasCreated: false);
}
