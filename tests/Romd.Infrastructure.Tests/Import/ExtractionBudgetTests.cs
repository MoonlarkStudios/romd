using Romd.Admin.Application.Ingestion.Extraction;
using Romd.Infrastructure.Import;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Import;

public sealed class ExtractionBudgetTests
{
    /// <summary>Budget of 10 entries and 900 bytes (1,000 available minus the 100-byte floor).</summary>
    private static ExtractionBudget CreateBudget() =>
        new(
            new ArchiveExtractionLimits { MaxTotalEntries = 10, SpaceFloorBytes = 100 },
            availableBytes: 1_000,
            stagedEntryCount: 0);

    [Fact]
    public void ValidateDeclared_TotalsAtRemaining_Succeeds()
    {
        var budget = CreateBudget();

        budget.ValidateDeclared("a.zip", declaredEntries: 10, declaredUncompressedBytes: 900)
            .IsError.ShouldBeFalse();
    }

    [Fact]
    public void ValidateDeclared_EntriesOverRemaining_ReturnsBudgetExceeded()
    {
        var result = CreateBudget().ValidateDeclared(
            "a.zip", declaredEntries: 11, declaredUncompressedBytes: 1);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.ExtractionBudgetExceeded");
        result.FirstError.Description.ShouldContain("a.zip");
    }

    [Fact]
    public void ValidateDeclared_BytesOverRemaining_ReturnsExpansionExceedsSpace()
    {
        var result = CreateBudget().ValidateDeclared(
            "a.zip", declaredEntries: 1, declaredUncompressedBytes: 901);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.ArchiveExpansionExceedsSpace");
        result.FirstError.Description.ShouldContain("uncompressed bytes");
    }

    [Fact]
    public void TryChargeEntry_ChargesUntilExhausted_ThenRefuses()
    {
        var budget = CreateBudget();

        for (int i = 0; i < 10; i++)
        {
            budget.TryChargeEntry().ShouldBeTrue();
        }

        budget.TryChargeEntry().ShouldBeFalse();
        budget.RemainingEntries.ShouldBe(0);
    }

    [Fact]
    public void TryChargeEntry_ShrinksBudgetSeenByNextArchivePrecheck()
    {
        var budget = CreateBudget();

        for (int i = 0; i < 6; i++)
        {
            budget.TryChargeEntry().ShouldBeTrue();
        }

        budget.RemainingEntries.ShouldBe(4);
        // The next archive is validated against the shrunk budget, not the initial one.
        budget.ValidateDeclared("b.zip", declaredEntries: 5, declaredUncompressedBytes: 1)
            .FirstError.Code.ShouldBe("Import.ExtractionBudgetExceeded");
    }

    [Fact]
    public void TryChargeBytes_ChunkCrossingRemaining_RefusedWithoutCharging()
    {
        var budget = CreateBudget();

        budget.TryChargeBytes(500).ShouldBeTrue();
        budget.TryChargeBytes(401).ShouldBeFalse();

        // The refused chunk charged nothing: the exact remainder is still spendable.
        budget.RemainingBytes.ShouldBe(400);
        budget.TryChargeBytes(400).ShouldBeTrue();
        budget.RemainingBytes.ShouldBe(0);
    }

    [Fact]
    public void ClampBytesToAvailableSpace_SpaceShrank_ReducesBudgetToCurrentSpaceMinusFloor()
    {
        var budget = CreateBudget();

        budget.ClampBytesToAvailableSpace(availableBytes: 500);

        budget.RemainingBytes.ShouldBe(400);
        budget.ValidateDeclared("a.zip", declaredEntries: 1, declaredUncompressedBytes: 401)
            .FirstError.Code.ShouldBe("Import.ArchiveExpansionExceedsSpace");
    }

    [Fact]
    public void ClampBytesToAvailableSpace_SpaceGrew_NeverRaisesBudget()
    {
        var budget = CreateBudget();
        budget.TryChargeBytes(500).ShouldBeTrue();

        budget.ClampBytesToAvailableSpace(availableBytes: 1_000_000);

        // Bytes already extracted stay charged: free space growing cannot refund them.
        budget.RemainingBytes.ShouldBe(400);
    }

    [Fact]
    public void ClampBytesToAvailableSpace_SpaceBelowFloor_ZeroesBudget()
    {
        var budget = CreateBudget();

        budget.ClampBytesToAvailableSpace(availableBytes: 99);

        budget.RemainingBytes.ShouldBe(0);
        budget.TryChargeBytes(1).ShouldBeFalse();
    }

    [Fact]
    public void Budget_AvailableSpaceBelowFloor_HasZeroByteBudget()
    {
        var budget = new ExtractionBudget(
            new ArchiveExtractionLimits { MaxTotalEntries = 10, SpaceFloorBytes = 100 },
            availableBytes: 50,
            stagedEntryCount: 0);

        budget.RemainingBytes.ShouldBe(0);
        budget.ValidateDeclared("a.zip", declaredEntries: 1, declaredUncompressedBytes: 1)
            .FirstError.Code.ShouldBe("Import.ArchiveExpansionExceedsSpace");
    }

    [Fact]
    public void Budget_StagedEntriesReduceEntryCeiling()
    {
        // MaxTotalEntries is a workspace ceiling: 9 staged entries leave exactly 1 for
        // extraction, so an archive declaring 5 is refused up front.
        var budget = new ExtractionBudget(
            new ArchiveExtractionLimits { MaxTotalEntries = 10, SpaceFloorBytes = 100 },
            availableBytes: 1_000,
            stagedEntryCount: 9);

        budget.RemainingEntries.ShouldBe(1);
        budget.ValidateDeclared("a.zip", declaredEntries: 5, declaredUncompressedBytes: 1)
            .FirstError.Code.ShouldBe("Import.ExtractionBudgetExceeded");
    }

    [Fact]
    public void Budget_StagedEntriesAtOrAboveCeiling_HasZeroEntryBudget()
    {
        var budget = new ExtractionBudget(
            new ArchiveExtractionLimits { MaxTotalEntries = 10, SpaceFloorBytes = 100 },
            availableBytes: 1_000,
            stagedEntryCount: 12);

        budget.RemainingEntries.ShouldBe(0);
        budget.TryChargeEntry().ShouldBeFalse();
        budget.ValidateDeclared("a.zip", declaredEntries: 1, declaredUncompressedBytes: 1)
            .FirstError.Code.ShouldBe("Import.ExtractionBudgetExceeded");
    }

    [Fact]
    public void QuotaEnforcingStream_WriteCrossingByteBudget_ThrowsAndRefusesWholeChunk()
    {
        var budget = CreateBudget();
        var inner = new MemoryStream();
        using var stream = new QuotaEnforcingStream(inner, budget);

        stream.Write(new byte[896]);

        // The chunk crossing the boundary is refused entirely: nothing reaches the inner stream
        // and the budget keeps its exact remainder, so bytes on disk never exceed the budget.
        Should.Throw<ExtractionQuotaExceededException>(() => stream.Write(new byte[8]));
        inner.Length.ShouldBe(896);
        budget.RemainingBytes.ShouldBe(4);
    }
}
