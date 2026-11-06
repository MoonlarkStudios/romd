using System.Collections.Immutable;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class RepositoryTransactionOwnershipArchitectureTests
{
    // Newly introduced protocol capabilities require exact ownership review. They are not
    // added to, or substituted for, the frozen repository and adapter debt baselines.
    private static readonly ImmutableArray<ReviewedTransactionBoundary> ReviewedProtocolExtensions =
    [
        Boundary(TransactionBoundaryKind.UnitOfWorkAdapter,
            "Consumer-owned security mutations delegate to the existing unit-of-work helper, preserving rollback and durable-result cleanup semantics without a Consumer-to-Admin application dependency.",
            Site("src/Romd.Persistence/Identity/ConsumerSecurityTransaction.cs", "ConsumerSecurityTransaction", "ExecuteAsync", "ExecuteInTransactionAsync")),
        Boundary(TransactionBoundaryKind.UnitOfWorkAdapter,
            "The shared authentication command owns session acceptance on both API hosts and delegates settlement to the same scoped unit of work before cookie or protocol issuance.",
            Site("src/Romd.Persistence/Identity/AccountSessionTransaction.cs", "AccountSessionTransaction", "ExecuteAsync", "ExecuteInTransactionAsync")),
        Boundary(TransactionBoundaryKind.BootstrapProtocol,
            "The existing locked catalog bootstrap owner flushes staged icon file identities before recording reference assets and typed catalog foreign keys; the bridge never flushes or commits.",
            Site("src/Romd.Persistence/ReferenceData/RomdCatalogImporter.cs", "RomdCatalogImporter", "ImportAsync", "SaveChangesAsync"))
    ];

    [Fact]
    public void ReviewedProtocolExtensions_HaveExactShapeAndDoNotReplaceMeasuredDebt()
    {
        var sites = ReviewedProtocolExtensions.SelectMany(boundary => boundary.Sites).ToArray();
        sites.Length.ShouldBe(3);
        sites.Sum(site => site.Count).ShouldBe(3);
        sites.Select(site => site.Key).Distinct().Count().ShouldBe(3);
        var measured = InitialDebt.Select(site => site.Key)
            .Concat(AdditionalTransactionBoundaries.SelectMany(boundary => boundary.Sites).Select(site => site.Key))
            .ToImmutableHashSet();
        sites.ShouldAllBe(site => !measured.Contains(site.Key));
        ReviewedProtocolExtensions.Select(boundary => boundary.Kind).ShouldBe([
            TransactionBoundaryKind.UnitOfWorkAdapter,
            TransactionBoundaryKind.UnitOfWorkAdapter,
            TransactionBoundaryKind.BootstrapProtocol
        ]);
        ReviewedProtocolExtensions.ShouldAllBe(boundary =>
            !string.IsNullOrWhiteSpace(boundary.Rationale) && !boundary.Sites.IsEmpty);
    }

    [Theory]
    [InlineData("await work.ExecuteInTransactionAsync(WriteAsync, logger, ct);")]
    [InlineData("await work.ExecuteInTransactionAsync<int>(WriteAsync, logger, ct);")]
    public void ObserveSource_DelegatedTransaction_RecordsTheCapability(string operation)
    {
        const string path = "src/Romd.Persistence/Feature/TransactionAdapter.cs";
        string source = $$"""
            public sealed class TransactionAdapter
            {
                public async Task ExecuteAsync() { {{operation}} }
            }
            """;

        var observed = GroupOccurrences(ObserveSource(source, path));

        AssertMatchesReviewedSites(observed.Values,
            [Site(path, "TransactionAdapter", "ExecuteAsync", "ExecuteInTransactionAsync")]);
    }
}
