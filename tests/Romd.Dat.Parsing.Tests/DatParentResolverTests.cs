using Shouldly;
using Xunit;

namespace Romd.Dat.Parsing.Tests;

public class DatParentResolverTests
{
    [Fact]
    public void Resolve_AgreeingCloneDeclarations_MatchesWithoutNormalizingNamesOrIds()
    {
        // Arrange
        DatParentDeclaration[] entries =
        [
            new("Clone", null, "Parent", "007"),
            new("Clone by ID", null, null, "7"),
            new("Clone by name", null, "Parent", null),
            new("Clone with padded ID", null, null, " 007 "),
            new("Parent", "007", null, null),
            new("parent", "7", null, null),
            new("Padded", " 007 ", null, null)
        ];

        // Act
        var results = DatParentResolver.Resolve(entries);

        // Assert
        results.Count.ShouldBe(4);
        results["Clone"].ShouldBe(new DatParentResolution(DatParentStatus.Resolved, "Parent"));
        results["Clone by ID"].ShouldBe(new DatParentResolution(DatParentStatus.Resolved, "parent"));
        results["Clone by name"].ShouldBe(new DatParentResolution(DatParentStatus.Resolved, "Parent"));
        results["Clone with padded ID"].ShouldBe(new DatParentResolution(DatParentStatus.Resolved, "Padded"));
    }

    [Theory]
    [InlineData("PARENT", null, DatParentStatus.Missing)]
    [InlineData(null, "id", DatParentStatus.Missing)]
    [InlineData("Parent", "unknown", DatParentStatus.Missing)]
    [InlineData("unknown", "ID", DatParentStatus.Missing)]
    [InlineData("", "ID", DatParentStatus.Missing)]
    [InlineData("Parent", "  ", DatParentStatus.Missing)]
    [InlineData(null, "", DatParentStatus.Missing)]
    [InlineData("Parent", "duplicate", DatParentStatus.Ambiguous)]
    [InlineData("Other parent", "ID", DatParentStatus.Conflicting)]
    public void Resolve_UnusableCloneDeclaration_ReturnsStatusWithoutGuessing(
        string? cloneOf,
        string? cloneOfId,
        DatParentStatus expected)
    {
        // Arrange
        DatParentDeclaration[] entries =
        [
            new("Clone", null, cloneOf, cloneOfId),
            new("Parent", "ID", null, null),
            new("Other parent", "duplicate", null, null),
            new("Duplicate ID", "duplicate", null, null),
            new("Blank ID", "", null, null),
            new("Whitespace ID", "  ", null, null)
        ];

        // Act
        var results = DatParentResolver.Resolve(entries);

        // Assert
        results.Count.ShouldBe(1);
        results["Clone"].ShouldBe(new DatParentResolution(expected, null));
    }

    [Fact]
    public void Resolve_CyclesAndCyclicAncestry_ReportsCycleWithoutAffectingOtherLinks()
    {
        // Arrange
        DatParentDeclaration[] entries =
        [
            new("Leading clone", null, null, "ID-A"),
            new("A", "ID-A", "B", null),
            new("B", null, null, "ID-A"),
            new("Self", null, "Self", null),
            new("Later clone", null, "B", null),
            new("Valid clone", null, "Root", null),
            new("Root", null, null, null)
        ];

        // Act
        var results = DatParentResolver.Resolve(entries);
        var reversed = DatParentResolver.Resolve(entries.Reverse().ToArray());

        // Assert
        var cycle = new DatParentResolution(DatParentStatus.Cycle, null);
        results["Leading clone"].ShouldBe(cycle);
        results["A"].ShouldBe(cycle);
        results["B"].ShouldBe(cycle);
        results["Self"].ShouldBe(cycle);
        results["Later clone"].ShouldBe(cycle);
        results["Valid clone"].ShouldBe(new DatParentResolution(DatParentStatus.Resolved, "Root"));
        results.ContainsKey("Root").ShouldBeFalse();
        reversed.Count.ShouldBe(results.Count);
        foreach (var (name, resolution) in results)
        {
            reversed[name].ShouldBe(resolution);
        }
    }

    [Fact]
    public void Resolve_LongChain_PreservesEachImmediateParent()
    {
        // Arrange
        const int linkCount = 30_000;
        var entries = new DatParentDeclaration[linkCount + 1];
        for (int index = 0; index < linkCount; index++)
        {
            entries[index] = new DatParentDeclaration($"Entry {index}", null, $"Entry {index + 1}", null);
        }

        entries[linkCount] = new DatParentDeclaration($"Entry {linkCount}", null, null, null);

        // Act
        var results = DatParentResolver.Resolve(entries);

        // Assert
        results.Count.ShouldBe(linkCount);
        results["Entry 0"].ShouldBe(new DatParentResolution(DatParentStatus.Resolved, "Entry 1"));
        results["Entry 29999"].ShouldBe(new DatParentResolution(DatParentStatus.Resolved, "Entry 30000"));
    }

    [Fact]
    public void Resolve_Canceled_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Should.Throw<OperationCanceledException>(() => DatParentResolver.Resolve([], cancellation.Token));
    }
}
