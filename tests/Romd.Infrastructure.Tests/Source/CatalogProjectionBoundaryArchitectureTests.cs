using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Romd.Admin.Application.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Source;

public sealed class CatalogProjectionBoundaryArchitectureTests
{
    [Fact]
    public void CatalogProjectionService_DoesNotQueryProviderPayloadSetsDirectly()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Romd.Infrastructure",
            "Source",
            "CatalogProjectionService.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();

        var directProviderSetReads = root
            .DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(access => access.Expression is IdentifierNameSyntax { Identifier.ValueText: "context" })
            .Select(access => access.Name.Identifier.ValueText)
            .Where(name => name.StartsWith("Dat", StringComparison.Ordinal)
                           && !string.Equals(name, "Database", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        directProviderSetReads.ShouldBeEmpty(
            "catalog projection must consume neutral source facts through its application port; " +
            "only the DAT adapter may query provider payload tables");
    }

    [Fact]
    public void CatalogProjectionContracts_LiveInCatalogBoundary_AndExposeOnlyNeutralFacts()
    {
        typeof(ICatalogProjectionService).Namespace.ShouldBe("Romd.Admin.Application.Catalog");
        typeof(ICatalogSourceSnapshotReader).Namespace.ShouldBe("Romd.Admin.Application.Catalog");
        typeof(ICatalogSourceSnapshotProvider).Namespace.ShouldBe("Romd.Admin.Application.Catalog");
        typeof(ICatalogPayloadAssertionReader).Namespace.ShouldBe("Romd.Admin.Application.Catalog");
        typeof(ICatalogPayloadAssertionProvider).Namespace.ShouldBe("Romd.Admin.Application.Catalog");
        typeof(ITitlePayloadAvailabilityProjection).Namespace.ShouldBe("Romd.Admin.Application.Catalog");

        var contractTypes = new[]
        {
            typeof(CatalogSourceSnapshot),
            typeof(CatalogSourceEntrySnapshot),
            typeof(CatalogSourceClaimSnapshot),
            typeof(CatalogSourceRequirementSnapshot),
            typeof(CatalogSourceRequirementKind),
            typeof(CatalogPayloadAssertion)
        };
        var leakedTypes = contractTypes
            .SelectMany(type => type.GetProperties().Select(property => property.PropertyType).Append(type))
            .Where(type => type.Namespace?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true
                           || type.Namespace?.StartsWith("Romd.Infrastructure", StringComparison.Ordinal) == true
                           || type.Namespace?.StartsWith("Romd.Persistence", StringComparison.Ordinal) == true
                           || type.FullName?.Contains(".Source.Dat.", StringComparison.Ordinal) == true)
            .Select(type => type.FullName)
            .ToArray();

        leakedTypes.ShouldBeEmpty("the catalog snapshot cannot expose EF or provider payload models");
        typeof(CatalogSourceEntrySnapshot).GetProperty(nameof(CatalogSourceEntrySnapshot.HasLocalPayload))
            .ShouldNotBeNull("local availability is a provider-neutral source-entry fact");
        typeof(CatalogPayloadAssertion).GetProperties()
            .Select(property => property.Name)
            .ShouldBe(["SourceEntryId", "AssertedTitleId", "HasLocalPayload"]);
        typeof(ICatalogPayloadAssertionProvider).GetProperty(
                nameof(ICatalogPayloadAssertionProvider.SourceKind))
            .ShouldNotBeNull("each provider kind must have one explicit assertion owner");
    }

    [Fact]
    public void TitleAvailabilityReads_DoNotRederiveProviderPayloadTruth()
    {
        string root = FindRepositoryRoot();
        var targets = new[]
        {
            ("TitleRepository.cs", new[]
            {
                "GetWithLocalPayloadByPlatformAsync",
                "GetWithLocalPayloadByPlatformFilteredAsync",
                "GetTitleIdsWithLocalPayloadAsync",
                "HasLocalPayloadAsync"
            }),
            ("RomRepository.cs", new[] { "GetPlatformBreakdownAsync" })
        };

        foreach (var (file, methodNames) in targets)
        {
            string path = Path.Combine(
                root, "src", "Romd.Persistence", "Repositories", file);
            var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();
            foreach (string methodName in methodNames)
            {
                var method = syntax.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .Single(candidate => candidate.Identifier.ValueText == methodName);
                string body = method.ToFullString();
                body.ShouldNotContain("DatRoms");
                body.ShouldNotContain("DatGames");
                body.ShouldNotContain("EffectiveTitleSourceLinks");
                body.ShouldContain("HasLocalPayload");
            }
        }
    }

    [Fact]
    public void SearchRepository_SeparatesProviderNeutralAvailabilityFromDatReleaseCompleteness()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Romd.Persistence",
            "Repositories",
            "SearchRepository.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();

        var projection = root
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method =>
                method.Identifier.ValueText == "ProjectToRankedCatalogTitles");
        string projectionBody = projection.ToFullString();
        projectionBody.ShouldContain("DatGames");
        projectionBody.ShouldContain("DatRoms");

        var availabilityAssignments = root
            .DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(assignment => assignment.Left.ToString() == "HasLocalPayload")
            .Select(assignment => assignment.Right.ToString())
            .ToArray();
        availabilityAssignments.ShouldBe(["t.HasLocalPayload"]);
    }

    [Fact]
    public void ScaleHarness_RemainsManualAndOutsideDefaultTestAndWorkflowExecution()
    {
        string root = FindRepositoryRoot();
        var defaultGateFiles = new[] { Path.Combine(root, ".mise.toml") }
            .Concat(Directory.GetFiles(Path.Combine(root, ".github", "workflows"), "*.*"));

        foreach (string path in defaultGateFiles)
        {
            File.ReadAllText(path).Contains("Romd.ScaleHarness", StringComparison.Ordinal).ShouldBeFalse(
                $"{Path.GetRelativePath(root, path)} must not make giant scale fixtures a PR/default-test gate");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Romd.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
