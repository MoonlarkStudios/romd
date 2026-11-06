using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>
///     Media policy decisions live in their registrations. Platform keys, the qualified CHD RID, stored-format
///     extensions and recipe constants may appear in runtime source only where these exact allow-lists say so.
///     Entries name registrations, verifier/protocol owners and content detection. Stale entries fail. Generated
///     migration snapshots are excluded; baseline SQL resources have a separate immutability guard.
/// </summary>
public sealed class MediaPolicyLiteralArchitectureTests
{
    private const string QualifiedCdRid = "linux-x64";

    private static readonly ImmutableHashSet<string> PlatformKeys = RomMediaPolicyComposition.Create().Kinds.Kinds
        .SelectMany(kind => kind.PlatformKeys).ToImmutableHashSet(StringComparer.Ordinal);

    private static readonly RomDeliveryPolicy DeliveryPolicy = RomMediaPolicyComposition.CreateDeliveryPolicy();

    private static readonly ImmutableHashSet<string> RuntimeIds = DeliveryPolicy.PlatformKeys
        .SelectMany(key => DeliveryPolicy.For(key)!.NativeRuntimes).ToImmutableHashSet(StringComparer.Ordinal);

    /// <summary>Stored-format extensions, plus <c>.iso</c>, the original extension delivery gives an encoded disc.</summary>
    private static readonly ImmutableArray<string> Extensions =
    [
        DeliveryPolicy.Formats.FileExtension(RomDeliveryFormat.Rvz), DeliveryPolicy.Formats.FileExtension(RomDeliveryFormat.Chd), ".iso"
    ];

    private static readonly ImmutableHashSet<string> RecipeTypes = ImmutableHashSet.Create(StringComparer.Ordinal,
        "RvzCanonicalRecipe", "RvzCanonicalBounds", "ChdDvdCanonicalRecipe", "ChdEncodingRecipe", "ChdDvdOutputBounds",
        "ChdCdEncodingRecipe");

    /// <summary>Codecs, recipe definitions and the media adapters, encoders and verifiers own their recipe constants.</summary>
    private static readonly ImmutableArray<string> RecipeOwners =
    [
        "src/Romd.Media/", "src/Romd.Admin.Application/Source/Rom/ChdEncodingRecipe.cs", "src/Romd.Admin.Application/Source/Rom/ChdDvdOutputBounds.cs",
        "src/Romd.Infrastructure/Storage/ChdCdArchiveWorkflow.cs",
        "src/Romd.Infrastructure/Storage/ChdArtifactEncoder.cs", // codec receipt checks, with no media-kind or ownership authority
        "src/Romd.Infrastructure/Storage/DvdChdEncodingProfile.cs",
        "src/Romd.Infrastructure/Storage/RvzEncodingProfile.cs", "src/Romd.Infrastructure/Storage/MediaWorkerSupervisor.cs",
        "src/Romd.Infrastructure/Storage/RvzVerifier.cs"
    ];

    private const string Registration = "media policy registration";
    private const string WorkerProtocol = "media-worker protocol value that shares a platform key's spelling";
    private const string UploadDetection = "upload detection selects a candidate adapter by extension; inspection decides";
    private const string ContentShape = "DAT content shape: entry points and single-image discs come from catalog file names";

    private static readonly ImmutableArray<Allowed> AllowList =
    [
        Permanent("src/Romd.Infrastructure/Storage/RomMediaPolicyComposition.cs", "gc", 1, Registration),
        Permanent("src/Romd.Infrastructure/Storage/RomMediaPolicyComposition.cs", "wii", 1, Registration),
        Permanent("src/Romd.Infrastructure/Storage/RomMediaPolicyComposition.cs", "ps2", 1, Registration),
        Permanent("src/Romd.Infrastructure/Storage/RomMediaPolicyComposition.cs", "psp", 1, Registration),
        Permanent("src/Romd.Infrastructure/Storage/RomMediaPolicyComposition.cs", "psx", 1, Registration),
        Permanent("src/Romd.Infrastructure/Storage/RomMediaPolicyComposition.cs", ".iso", 1, Registration),
        Permanent("src/Romd.Infrastructure/Storage/RomMediaPolicyComposition.cs", "RvzCanonicalRecipe", 4, Registration),
        Permanent("src/Romd.Infrastructure/Storage/RomMediaPolicyComposition.cs", "ChdDvdCanonicalRecipe", 2, Registration),
        Permanent("src/Romd.Infrastructure/Storage/RomMediaPolicyComposition.cs", "ChdCdEncodingRecipe", 5, Registration),
        Permanent("src/Romd.Infrastructure/Storage/RomMediaPolicyComposition.cs", "dolphin", 1, Registration),
        Permanent("src/Romd.Infrastructure/Storage/RomMediaPolicyComposition.cs", "pcsx2", 1, Registration),
        Permanent("src/Romd.Infrastructure/Storage/RomMediaPolicyComposition.cs", ".rvz", 1, Registration),
        Permanent("src/Romd.Infrastructure/Storage/RomMediaPolicyComposition.cs", ".chd", 1, Registration),
        Permanent("src/Romd.Infrastructure/Storage/RomMediaPolicyComposition.cs", QualifiedCdRid, 1,
            "the registrations declare qualified CD decoding and PSP encoding without duplicating runtime policy"),
        Permanent("src/Romd.Media/Inspection/MediaWorkerProtocol.cs", "wii", 4, WorkerProtocol),
        Permanent("src/Romd.Media/Rvz/RvzCodecFamily.cs", "wii", 1, "the codec family owns the worker argument"),
        Permanent("src/Moonlark.Libchdr/Internal/NativePlatform.cs", QualifiedCdRid, 2,
            "the native loader maps the running process to its library file"),
        Permanent("src/Romd.Infrastructure/Storage/IsoPreparationAdapter.cs", ".iso", 2, UploadDetection),
        Permanent("src/Romd.Infrastructure/Storage/RomEncodingComposition.cs", ".rvz", 1, "the encoding registration owns its upload signature and filename hint"),
        Permanent("src/Romd.Infrastructure/Storage/RvzPreparationAdapter.cs", ".iso", 1, UploadDetection),
        Permanent("src/Romd.Infrastructure/Storage/RomEncodingComposition.cs", ".chd", 1, "the encoding registration owns its upload signature and filename hint"),
        Permanent("src/Romd.Infrastructure/Storage/ChdPreparationAdapter.cs", ".iso", 1, UploadDetection),
        Permanent("src/Romd.Infrastructure/Storage/RomPreparationDetection.cs", ".iso", 3, UploadDetection),
        Permanent("src/Romd.Persistence/Repositories/ConsumerReleaseManifestRepository.cs", ".chd", 1, ContentShape),
        Permanent("src/Romd.Persistence/Repositories/ConsumerReleaseManifestRepository.cs", ".iso", 1, ContentShape),
        Permanent("src/Romd.Persistence/Repositories/ConsumerDiscSetRepository.cs", ".iso", 1, ContentShape)
    ];

    [Fact]
    public void MediaPolicyLiterals_AppearOnlyInTheirAllowedPlaces()
    {
        var observed = RuntimeSources().SelectMany(source => Occurrences(source.Path, source.Root))
            .Select(Describe).Order(StringComparer.Ordinal).ToArray();
        var allowed = AllowList.Select(entry => Describe((entry.Path, entry.Value, entry.Count)))
            .Order(StringComparer.Ordinal).ToArray();

        observed.ShouldBe(allowed, "Move the decision into its media registration, or record why it belongs here.");
    }

    private static readonly ImmutableHashSet<string> Profiles = RomMediaPolicyComposition.Create().Kinds.Kinds
        .SelectMany(kind => kind.ProfileVersions).ToImmutableHashSet(StringComparer.Ordinal);

    private static readonly (string Path, string Value, int Count)[] ProfileOwners =
    [
        ("src/Romd.Infrastructure/Storage/RomMediaPolicyComposition.cs", "rvz-import/v1", 1),
        ("src/Romd.Infrastructure/Storage/RomMediaPolicyComposition.cs", "rvz-canonical/v1", 1),
        ("src/Romd.Infrastructure/Storage/RomMediaPolicyComposition.cs", "chd-observed/v1", 1),
        ("src/Romd.Infrastructure/Storage/ChdVerifier.cs", "chd-observed/v1", 1),
        ("src/Romd.Media/Inspection/MediaWorkerProtocol.cs", "rvz-import/v1", 1),
        ("src/Romd.Media/Inspection/RvzInspectionProfile.cs", "rvz-import/v1", 2),
        ("src/Romd.Media/Rvz/RvzCanonicalRecipe.cs", "rvz-canonical/v1", 1),
        ("src/Romd.Media/PlayStation/ChdCdJobs.cs", "chd-cd-whole-files/v1", 1),
        ("src/Romd.Media/PlayStation/ChdCdRetainedVerification.cs", "chd-cd-retained-files/v2", 1),
        ("src/Romd.Media/PlayStation/ChdCdInspectionService.cs", "chd-cd-inspection/v2", 1),
        ("src/Romd.Application.Common/Content/IRomContentReader.cs", "rvz-import/v1", 1),
        ("src/Romd.Application.Common/Content/IRomContentReader.cs", "chd-observed/v1", 1),
        ("src/Romd.Admin.Application/Source/Rom/ChdEncodingRecipe.cs", "chd-observed/v1", 1)
    ];

    [Fact]
    public void RegisteredProfiles_AppearOnlyInRegistrationsVerifiersAndProtocolOwners()
    {
        var observed = RuntimeSources().SelectMany(source => ProfileOccurrences(source.Path, source.Root))
            .Select(Describe).Order(StringComparer.Ordinal).ToArray();
        observed.ShouldBe(ProfileOwners.Select(Describe).Order(StringComparer.Ordinal).ToArray(),
            "Read registered profiles through the policy; only declarations, verifier protocols and observed parameter labels own literals.");
    }

    [Fact]
    public void ProfileOccurrences_FindsCopiedProfilesAndParameterizedLabels()
    {
        var found = ProfileOccurrences("src/Planner.cs", Parse("""
            class A { string Profile = "chd-cd-whole-files/v1"; string Label = $"rvz-import/v1;codec={codec}"; }
            """)).OrderBy(value => value.Value, StringComparer.Ordinal).ToArray();
        found.ShouldBe([("src/Planner.cs", "chd-cd-whole-files/v1", 1), ("src/Planner.cs", "rvz-import/v1", 1)]);
    }

    private static IEnumerable<(string Path, string Value, int Count)> ProfileOccurrences(string path, CompilationUnitSyntax root) =>
        root.DescendantTokens().Where(IsStringToken).Select(token => token.ValueText)
            .SelectMany(value => Profiles.Where(profile => value == profile || value.StartsWith(profile + ";", StringComparison.Ordinal)))
            .GroupBy(value => value, StringComparer.Ordinal).Select(group => (path, group.Key, group.Count()));

    [Fact]
    public void AllowList_NamesEachPlaceOnceWithAReason()
    {
        AllowList.GroupBy(entry => (entry.Path, entry.Value)).ShouldAllBe(group => group.Count() == 1);
        AllowList.ShouldAllBe(entry => entry.Count > 0 && !string.IsNullOrWhiteSpace(entry.Reason));
    }

    [Fact]
    public void Occurrences_FindsKeysInEveryStringForm() =>
        Occurrences("src/Example.cs", Parse(""""
            class A
            {
                string K = "ps2";
                string I = $"{"wii"}";
                string R = """gc""";
                string U = "DecoderRid = 'linux-x64'";
                string N = "pcsx2";
                bool S(string key) => key is "ps2";
            }
            """")).OrderBy(found => found.Value, StringComparer.Ordinal)
            .ShouldBe([("src/Example.cs", "gc", 1), ("src/Example.cs", QualifiedCdRid, 1), ("src/Example.cs", "pcsx2", 1),
                ("src/Example.cs", "ps2", 2), ("src/Example.cs", "wii", 1)]);

    [Fact]
    public void Occurrences_FindsExtensionsInFilenamesButNotOtherWords() =>
        Occurrences("src/Example.cs", Parse("""
            class A
            {
                string E = ".chd";
                string F = "original.ISO";
                string I = $"{name}.rvz";
                string T = "chdman";
                string W = "rvz";
            }
            """)).OrderBy(found => found.Value, StringComparer.Ordinal)
            .ShouldBe([("src/Example.cs", ".chd", 1), ("src/Example.cs", ".rvz", 1), ("src/Example.cs", "original.ISO", 1)]);

    [Fact]
    public void Occurrences_FindsRecipeConstantsOnlyOutsideTheirOwners()
    {
        const string source = "class A { uint H = ChdEncodingRecipe.HunkBytes; string R = Romd.Media.Rvz.RvzCanonicalRecipe.Id; }";
        Occurrences("src/Romd.Persistence/Example.cs", Parse(source)).OrderBy(found => found.Value, StringComparer.Ordinal)
            .ShouldBe([("src/Romd.Persistence/Example.cs", "ChdEncodingRecipe", 1),
                ("src/Romd.Persistence/Example.cs", "RvzCanonicalRecipe", 1)]);
        Occurrences("src/Romd.Infrastructure/Storage/ChdReadAdmission.cs", Parse(source)).Count().ShouldBe(2);
        Occurrences("src/Romd.Infrastructure/Storage/RvzVerifier.cs", Parse(source)).ShouldBeEmpty();
        Occurrences("src/Romd.Infrastructure/Storage/ChdArtifactEncoder.cs", Parse(source)).ShouldBeEmpty();
        Occurrences("src/Romd.Infrastructure/Storage/ChdArtifactEncoder.cs", Parse("class A { string Platform = \"ps2\"; }"))
            .ShouldBe([("src/Romd.Infrastructure/Storage/ChdArtifactEncoder.cs", "ps2", 1)]);
        Occurrences("src/Romd.Media/Example.cs", Parse(source)).ShouldBeEmpty();
    }

    private static IEnumerable<(string Path, string Value, int Count)> Occurrences(string path, CompilationUnitSyntax root)
    {
        var literals = root.DescendantTokens().Where(IsStringToken).Select(token => token.ValueText).SelectMany(Matches);
        var recipes = RecipeOwners.Any(owner => path.StartsWith(owner, StringComparison.Ordinal))
            ? []
            : root.DescendantNodes().OfType<IdentifierNameSyntax>().Select(name => name.Identifier.ValueText)
                .Where(RecipeTypes.Contains);
        return literals.Concat(recipes).GroupBy(value => value, StringComparer.Ordinal)
            .Select(group => (path, group.Key, group.Count()));
    }

    /// <summary>Keys and runtimes match exactly, the RID anywhere in a string and extensions at the end of a filename.</summary>
    private static IEnumerable<string> Matches(string value)
    {
        if (PlatformKeys.Contains(value) || RuntimeIds.Contains(value))
            yield return value;
        if (value.Contains(QualifiedCdRid, StringComparison.Ordinal))
            yield return QualifiedCdRid;
        if (Extensions.Any(extension => value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            yield return value;
    }

    private static bool IsStringToken(SyntaxToken token) =>
        token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.InterpolatedStringTextToken) ||
        token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken) || token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken) ||
        token.IsKind(SyntaxKind.Utf8StringLiteralToken);

    private static string Describe((string Path, string Value, int Count) occurrence) =>
        $"{occurrence.Path}: \"{occurrence.Value}\" x{occurrence.Count}";

    private static Allowed Permanent(string path, string value, int count, string reason) => new(path, value, count, reason);

    private sealed record Allowed(string Path, string Value, int Count, string Reason);

    private static CompilationUnitSyntax Parse(string source) => CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

    private static IEnumerable<(string Path, CompilationUnitSyntax Root)> RuntimeSources()
    {
        string root = FindRepositoryRoot();
        return Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !path.Split('/').Any(segment => segment is "bin" or "obj" or "Migrations"))
            .Order(StringComparer.Ordinal)
            .Select(path => (path, Parse(File.ReadAllText(Path.Combine(root, path)))));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Romd.sln")))
                return directory.FullName;
        throw new InvalidOperationException("Could not find the repository root.");
    }
}
