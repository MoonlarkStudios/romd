using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.Inspection;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>Guards the ownership boundaries behind the shared media registration.</summary>
public sealed class MediaOwnershipArchitectureTests
{
    private static readonly string[] RvzOrchestrators =
    [
        "src/Romd.Infrastructure/Storage/RvzEncodingService.cs",
        "src/Romd.Infrastructure/Storage/MediaWorkerSupervisor.cs",
        "src/Romd.Infrastructure/Storage/RvzVerifier.cs",
        "src/Romd.Infrastructure/Storage/MediaReconciliationMediaProfile.cs",
        "src/Romd.Media/Inspection/MediaEncodingService.cs",
        "src/Romd.Media.Worker/Program.cs"
    ];

    [Fact]
    public void KindPolicies_DoNotDependOnAnotherConcreteKindPolicy()
    {
        var policies = typeof(RomMediaPolicyComposition).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(IRomMediaTypePolicy).IsAssignableFrom(type))
            .Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        policies.ShouldNotBeEmpty();
        var dependencies = RuntimeSources().SelectMany(source => source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(type => policies.Contains(type.Identifier.ValueText))
                .SelectMany(type => type.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Where(name => policies.Contains(name.Identifier.ValueText) && name.Identifier.ValueText != type.Identifier.ValueText)
                    .Select(name => $"{source.Path}: {type.Identifier.ValueText} -> {name.Identifier.ValueText}")))
            .Distinct().Order(StringComparer.Ordinal).ToArray();
        dependencies.ShouldBeEmpty("Kinds compose a shared encoding profile instead of another system's policy.");
    }

    [Fact]
    public void RvzOrchestration_DelegatesFamilyDecisionsToTheCodecOwner()
    {
        var violations = RvzOrchestrators.SelectMany(path => FamilySelections(Parse(Read(path)))
            .Select(selection => $"{path}: {selection}")).ToArray();
        violations.ShouldBeEmpty("The codec family owns encoder identity, bounds, worker tokens and execution selection.");
    }

    [Fact]
    public void SharedChdExecutionAndCdConsumers_DoNotDependOnAnEncoderService()
    {
        string[] shared =
        [
            "src/Romd.Media/Chd/ChdmanToolRunner.cs",
            "src/Romd.Media/Chd/ChdToolPaths.cs"
        ];
        var sharedReferences = shared.SelectMany(path => References(path, "ChdDvdEncodingService", "ChdCdEncodingService"));
        string[] cd =
        [
            "src/Romd.Media/Cd/ChdCdEncodingService.cs",
            "src/Romd.Media/Cd/ChdCdJobs.cs"
        ];
        var cdReferences = cd.SelectMany(path => References(path, "ChdDvdEncodingService"));
        sharedReferences.Concat(cdReferences).ShouldBeEmpty("Shared lifecycle ownership belongs to the runner, not one of its consumers.");
    }

    [Fact]
    public void MediaCodecs_DoNotReferenceDomainApplicationOrInfrastructureAssemblies()
    {
        var forbidden = typeof(MediaWorkerProtocol).Assembly.GetReferencedAssemblies().Select(reference => reference.Name)
            .Where(name => name == typeof(RomLogicalInspection).Assembly.GetName().Name ||
                name == typeof(IRomMediaTypePolicy).Assembly.GetName().Name ||
                name == typeof(RomMediaPolicyComposition).Assembly.GetName().Name ||
                name?.StartsWith("Romd.Application.", StringComparison.Ordinal) == true ||
                name?.StartsWith("Romd.Consumer.Application", StringComparison.Ordinal) == true ||
                name?.StartsWith("Romd.Persistence", StringComparison.Ordinal) == true ||
                name?.StartsWith("Romd.Hosting", StringComparison.Ordinal) == true ||
                name?.EndsWith(".Host", StringComparison.Ordinal) == true)
            .ToArray();
        forbidden.ShouldBeEmpty("Infrastructure translates domain kinds; codecs stay independent of those layers.");
    }

    [Fact]
    public void FamilySelections_FindsBranchesAndEncoderChoicesButIgnoresCommentsAndLabels()
    {
        FamilySelections(Parse("""
            class A {
                // RomIsoDiscKind.GameCube and RvzEncoder belong to this comment only.
                string Label = "RvzWiiEncoder";
                bool IsCube = kind == RomIsoDiscKind.GameCube;
                object Encoder = new RvzWiiEncoder();
                object Bound = RvzCanonicalBounds.GetGameCubeOutputBound(1, 2);
            }
            """)).ShouldBe(["RomIsoDiscKind.GameCube", "RvzWiiEncoder", "RvzCanonicalBounds"]);
    }

    private static IEnumerable<string> FamilySelections(CompilationUnitSyntax root)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (node is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText is "GameCube" or "Wii" &&
                (member.Expression.ToString().EndsWith("RomIsoDiscKind", StringComparison.Ordinal) ||
                 member.Expression.ToString().EndsWith("NintendoDiscKind", StringComparison.Ordinal)))
                yield return member.ToString();
            if (node is IdentifierNameSyntax identifier && identifier.Identifier.ValueText is "RvzEncoder" or "RvzWiiEncoder" or "RvzCanonicalBounds")
                yield return identifier.Identifier.ValueText;
        }
    }

    private static IEnumerable<string> References(string path, params string[] names) =>
        Parse(Read(path)).DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(name => names.Contains(name.Identifier.ValueText, StringComparer.Ordinal))
            .Select(name => $"{path}: {name.Identifier.ValueText}");

    private static IEnumerable<(string Path, CompilationUnitSyntax Root)> RuntimeSources()
    {
        string root = FindRepositoryRoot();
        return Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj" or "Migrations"))
            .Select(path => (Path.GetRelativePath(root, path), Parse(File.ReadAllText(path))));
    }

    private static CompilationUnitSyntax Parse(string source) => CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
    private static string Read(string path) => File.ReadAllText(Path.Combine(FindRepositoryRoot(), path));
    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Romd.sln")))
                return directory.FullName;
        throw new InvalidOperationException("Could not find the repository root.");
    }
}
