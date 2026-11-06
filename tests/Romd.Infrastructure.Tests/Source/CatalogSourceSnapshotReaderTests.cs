using System.Collections.Immutable;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Source;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Source;

public sealed class CatalogSourceSnapshotReaderTests
{
    [Fact]
    public async Task ReadPlatformAsync_MultipleProviders_OrdersClaimsDeterministically()
    {
        var first = ProviderWith(Entry(CatalogSourceKind.Manual, 20, Claim("4", claimOrder: 4)));
        var second = ProviderWith(
            Entry(CatalogSourceKind.Import, 30, Claim("9", claimOrder: 9)),
            Entry(CatalogSourceKind.Import, 10, Claim("8", claimOrder: 8)));
        var reader = new CatalogSourceSnapshotReader([first, second]);

        var result = await reader.ReadPlatformAsync(7);

        result.Entries.Select(entry => entry.SourceEntryId).ShouldBe([10, 30, 20]);
    }

    [Fact]
    public async Task ReadPlatformAsync_ProvidersOverlapSourceEntry_RejectsAmbiguousOwnership()
    {
        var first = ProviderWith(Entry(CatalogSourceKind.Dat, 20, Claim("4")));
        var second = ProviderWith(Entry(CatalogSourceKind.Import, 20, Claim("9")));
        var reader = new CatalogSourceSnapshotReader([first, second]);

        var exception = await Should.ThrowAsync<InvalidOperationException>(reader.ReadPlatformAsync(7));

        exception.Message.ShouldContain("source entry 20");
    }

    [Fact]
    public async Task ReadPlatformAsync_OneProviderRepeatsSourceEntry_RejectsDuplicateEntry()
    {
        var provider = ProviderWith(
            Entry(CatalogSourceKind.Import, 20, Claim("first")),
            Entry(CatalogSourceKind.Import, 20, Claim("second")));
        var reader = new CatalogSourceSnapshotReader([provider]);

        var exception = await Should.ThrowAsync<InvalidOperationException>(reader.ReadPlatformAsync(7));

        exception.Message.ShouldContain("returned more than once");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ReadPlatformAsync_NonPositiveSourceEntryId_RejectsInvalidIdentity(int sourceEntryId)
    {
        var reader = new CatalogSourceSnapshotReader([
            ProviderWith(Entry(CatalogSourceKind.Import, sourceEntryId, Claim("claim")))
        ]);

        await Should.ThrowAsync<InvalidOperationException>(reader.ReadPlatformAsync(7));
    }

    [Fact]
    public async Task ReadPlatformAsync_UndefinedSourceOrRequirementKind_RejectsInvalidFacts()
    {
        var invalidSource = new CatalogSourceSnapshotReader([
            ProviderWith(Entry((CatalogSourceKind)999, 20, Claim("claim")))
        ]);
        var invalidRequirement = new CatalogSourceRequirementSnapshot(
            "requirement", 1, (CatalogSourceRequirementKind)999, "file", 1, null, null, null, null);
        var invalidClaim = Claim("claim") with { Requirements = [invalidRequirement] };
        var invalidRequirementReader = new CatalogSourceSnapshotReader([
            ProviderWith(Entry(CatalogSourceKind.Import, 20, invalidClaim))
        ]);

        await Should.ThrowAsync<InvalidOperationException>(invalidSource.ReadPlatformAsync(7));
        await Should.ThrowAsync<InvalidOperationException>(invalidRequirementReader.ReadPlatformAsync(7));
    }

    [Fact]
    public async Task ReadPlatformAsync_OneProviderHasMultipleClaimsForEntry_PreservesEveryVersionClaim()
    {
        var provider = ProviderWith(
            Entry(
                CatalogSourceKind.Dat,
                20,
                Claim("9", claimOrder: 9),
                Claim("4", claimOrder: 4)));
        var reader = new CatalogSourceSnapshotReader([provider]);

        var result = await reader.ReadPlatformAsync(7);

        result.Entries.Single().Claims.Select(claim => claim.ProviderClaimKey).ShouldBe(["9", "4"]);
    }

    [Fact]
    public async Task ReadPlatformAsync_CanceledBeforeProviders_DoesNotReadProvider()
    {
        var provider = Substitute.For<ICatalogSourceSnapshotProvider>();
        var reader = new CatalogSourceSnapshotReader([provider]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            reader.ReadPlatformAsync(7, cancellation.Token));

        await provider.DidNotReceiveWithAnyArgs().ReadPlatformAsync(default, default);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task ReadPlatformAsync_InvalidProviderClaimKey_RejectsAtBoundary(string? key)
    {
        var provider = ProviderWith(Entry(CatalogSourceKind.Import, 20, Claim(key!)));
        var reader = new CatalogSourceSnapshotReader([provider]);

        await Should.ThrowAsync<InvalidOperationException>(reader.ReadPlatformAsync(7));
    }

    [Fact]
    public async Task ReadPlatformAsync_ProviderKeysUseExactOrdinalIdentity()
    {
        var provider = ProviderWith(Entry(
            CatalogSourceKind.Import,
            20,
            Claim("claim"),
            Claim("CLAIM")));
        var reader = new CatalogSourceSnapshotReader([provider]);

        var result = await reader.ReadPlatformAsync(7);

        result.Entries.Single().Claims.Select(claim => claim.ProviderClaimKey).ShouldBe(["CLAIM", "claim"]);
    }

    [Fact]
    public async Task ReadPlatformAsync_DatAndImportReuseProviderKeys_EntryScopeKeepsBoth()
    {
        var requirement = new CatalogSourceRequirementSnapshot(
            "1", 1, CatalogSourceRequirementKind.Rom, "payload", 1, null, null, null, null);
        var datClaim = Claim("1") with { Requirements = [requirement] };
        var importClaim = Claim("1") with { Requirements = [requirement] };
        var reader = new CatalogSourceSnapshotReader([
            ProviderWith(Entry(CatalogSourceKind.Dat, 10, datClaim)),
            ProviderWith(Entry(CatalogSourceKind.Import, 20, importClaim))
        ]);

        var result = await reader.ReadPlatformAsync(7);

        result.Entries.Length.ShouldBe(2);
        result.Entries.ShouldAllBe(entry => entry.Claims.Single().ProviderClaimKey == "1");
        result.Entries.ShouldAllBe(entry =>
            entry.Claims.Single().Requirements.Single().ProviderRequirementKey == "1");
    }

    [Fact]
    public async Task ReadPlatformAsync_MaximumUnicodeProviderKey_IsAcceptedWithoutNormalization()
    {
        string key = string.Concat(Enumerable.Repeat("é", 200));
        var reader = new CatalogSourceSnapshotReader([
            ProviderWith(Entry(CatalogSourceKind.Import, 20, Claim(key)))
        ]);

        var result = await reader.ReadPlatformAsync(7);

        result.Entries.Single().Claims.Single().ProviderClaimKey.ShouldBe(key);
    }

    [Theory]
    [InlineData(201)]
    [InlineData(400)]
    public async Task ReadPlatformAsync_OverlengthProviderClaimKey_IsRejected(int length)
    {
        var reader = new CatalogSourceSnapshotReader([
            ProviderWith(Entry(CatalogSourceKind.Import, 20, Claim(new string('x', length))))
        ]);

        await Should.ThrowAsync<InvalidOperationException>(reader.ReadPlatformAsync(7));
    }

    [Fact]
    public async Task ReadPlatformAsync_MigrationReservedProviderKey_IsRejected()
    {
        var reader = new CatalogSourceSnapshotReader([
            ProviderWith(Entry(
                CatalogSourceKind.Import,
                20,
                Claim("~romd-migration-unresolved-datfile:7")))
        ]);

        var exception = await Should.ThrowAsync<InvalidOperationException>(reader.ReadPlatformAsync(7));

        exception.Message.ShouldContain("reserved catalog migration prefix");
    }

    private static ICatalogSourceSnapshotProvider ProviderWith(params CatalogSourceEntrySnapshot[] entries)
    {
        var provider = Substitute.For<ICatalogSourceSnapshotProvider>();
        provider.ReadPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new CatalogSourceSnapshot(entries.ToImmutableArray()));
        return provider;
    }

    private static CatalogSourceEntrySnapshot Entry(
        CatalogSourceKind sourceKind,
        int sourceEntryId,
        params CatalogSourceClaimSnapshot[] claims) =>
        new(sourceEntryId, sourceKind, AssertedTitleId: null, HasLocalPayload: false, claims.ToImmutableArray());

    private static CatalogSourceClaimSnapshot Claim(
        string providerClaimKey,
        int claimPrecedence = 0,
        long claimOrder = 0) =>
        new(
            providerClaimKey,
            claimPrecedence,
            claimOrder,
            Name: $"Claim {providerClaimKey}",
            Region: null,
            Language: null,
            Revision: null,
            ImmutableArray<CatalogSourceRequirementSnapshot>.Empty,
            ImmutableArray<int>.Empty,
            ImmutableArray<int>.Empty);
}
