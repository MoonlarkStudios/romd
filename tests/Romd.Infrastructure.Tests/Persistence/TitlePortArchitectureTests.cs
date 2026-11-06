using System.Reflection;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>
///     Guards the title use-case ports against regrowing into a broad repository. Each port's member
///     inventory is pinned, so adding, removing, or reshaping a member is a deliberate, reviewed edit.
/// </summary>
public sealed class TitlePortArchitectureTests
{
    private static readonly IReadOnlyDictionary<Type, string[]> ReadPortInventory = new Dictionary<Type, string[]>
    {
        [typeof(ITitleReader)] =
        [
            "Task<IReadOnlyList<Int32>> GetIdsByPlatformAsync(Int32 platformId, CancellationToken cancellationToken)",
            "Task<PagedList<TitleSummary>> ListByPlatformAsync(PlatformTitlePageRequest request, CancellationToken cancellationToken)",
            "Task<TitleSummary> GetSummaryAsync(Int32 titleId, CancellationToken cancellationToken)",
            "Task<Nullable<Guid>> GetRevisionAsync(Int32 titleId, CancellationToken cancellationToken)"
        ],
        [typeof(ITitleDetailReader)] =
        [
            "Task<TitleDetailData> GetDetailAsync(Int32 titleId, CancellationToken cancellationToken)",
            "Task<TitleDetailData> GetPublicDetailAsync(Int32 titleId, CancellationToken ct)",
            "Task<ErrorOr<TitleReadPage<TitleMediaData>>> GetMediaPageAsync(Int32 titleId, String cursor, Int32 limit, CancellationToken ct)",
            "Task<ErrorOr<TitleReadPage<TitleReleaseData>>> GetReleasePageAsync(Int32 titleId, String cursor, Int32 limit, CancellationToken ct)",
            "Task<ErrorOr<TitleReadPage<TitleReleaseSourceData>>> GetReleaseSourcesPageAsync(Int32 titleId, String releaseId, String cursor, Int32 limit, CancellationToken ct)"
        ],
        [typeof(ITitleMediaReader)] =
        [
            "Task<IReadOnlyList<TitleMediaSummary>> ListAsync(Int32 titleId, CancellationToken cancellationToken)"
        ],
        [typeof(ITitleEnrichmentStatusReader)] =
        [
            "Task<IReadOnlyList<EnrichmentCandidate>> GetCandidatesAsync(Int32 platformId, EnrichmentScope scope, CancellationToken cancellationToken)",
            "Task<TitleEnrichmentStats> GetStatsAsync(CancellationToken cancellationToken)"
        ],
        [typeof(ITrackedTitleReader)] =
        [
            "Task<Boolean> HasTrackedAsync(IReadOnlyCollection<Int32> titleIds, CancellationToken cancellationToken)"
        ]
    };

    private static readonly IReadOnlyDictionary<Type, string[]> StorePortInventory = new Dictionary<Type, string[]>
    {
        [typeof(ITitleRowLock)] =
        [
            "Task<Boolean> LockAsync(Int32 titleId, CancellationToken cancellationToken)",
            "Task<Nullable<Guid>> LockRevisionAsync(Int32 titleId, CancellationToken cancellationToken)"
        ],
        [typeof(ITitleMediaStore)] =
        [
            "Task<StagedMediaAppend> AddUserMediaStagedAsync(TitleMedia media, CancellationToken cancellationToken)",
            "Task<StagedMediaAppend> AppendGalleryMediaStagedAsync(TitleMedia media, CancellationToken cancellationToken)",
            "Task RemoveMediaStagedAsync(Title title, Int32 mediaId, CancellationToken cancellationToken)",
            "Task UpdatePrimaryMediaStagedAsync(Title title, MediaType type, CancellationToken cancellationToken)"
        ],
        [typeof(ITitleMergeStore)] =
        [
            "Task ApplyMergeStagedAsync(Title target, Title source, CancellationToken cancellationToken)",
            "Task<IReadOnlyList<String>> GetUnlinkedProvidersAsync(Int32 titleId, CancellationToken cancellationToken)",
            "Task DeleteAbsorbedSourceAsync(Int32 sourceTitleId, CancellationToken cancellationToken)",
            "Task StageArtworkMergeAsync(Int32 sourceTitleId, Int32 targetTitleId, CancellationToken cancellationToken)"
        ],
        [typeof(ITitleCreationStore)] =
        [
            "Task<Boolean> ExistsWithNormalizedNameAsync(Int32 platformId, String normalizedName, CancellationToken cancellationToken)",
            "Task<IReadOnlyDictionary<String, Int32>> FindIdsByNormalizedNamesAsync(Int32 platformId, IReadOnlyCollection<String> normalizedNames, CancellationToken cancellationToken)",
            "Task<IStagedTitleCreation> AddStagedAsync(Title title, CancellationToken cancellationToken)",
            "Task<IStagedTitleCreationBatch> AddBatchStagedAsync(IReadOnlyList<Title> titles, CancellationToken cancellationToken)"
        ],
        [typeof(ITitleOrphanCleanup)] =
        [
            "Task<Boolean> DeleteOrRetainAsync(Int32 titleId, CancellationToken cancellationToken)",
            "Task<Boolean> HasSourceEntriesAsync(Int32 titleId, CancellationToken cancellationToken)",
            "Task<IReadOnlyList<Int32>> GetOrphanedTitleIdsAsync(DateTimeOffset createdBefore, Int32 limit, CancellationToken cancellationToken)"
        ],
        [typeof(ITitleMetadataStore)] =
        [
            "Task ApplyEnrichmentStagedAsync(Title title, CancellationToken cancellationToken)",
            "Task UpdateMaterializedMetadataStagedAsync(Title title, CancellationToken cancellationToken)",
            "Task UpdateUserMetadataStagedAsync(Title title, CancellationToken cancellationToken)"
        ]
    };

    /// <summary>The one aggregate load: read-only, and the only member allowed to return a Title.</summary>
    private static readonly IReadOnlyDictionary<Type, string[]> AggregatePortInventory = new Dictionary<Type, string[]>
    {
        [typeof(ITitleAggregateReader)] =
        [
            "Task<Title> GetAggregateAsync(Int32 titleId, CancellationToken cancellationToken)"
        ]
    };

    private static IEnumerable<KeyValuePair<Type, string[]>> AllPorts =>
        ReadPortInventory.Concat(StorePortInventory).Concat(AggregatePortInventory);

    private static readonly string[] WriteVerbs = ["Add", "Update", "Apply", "Delete", "Remove", "Stage", "Mark", "Lock", "Set", "Track", "Untrack"];

    [Fact]
    public void TitlePorts_MatchPinnedMemberInventory()
    {
        foreach (var (port, expected) in AllPorts)
        {
            port.GetMethods().Select(Signature).Order(StringComparer.Ordinal).ToArray()
                .ShouldBe(expected.Order(StringComparer.Ordinal).ToArray(), port.Name);
            port.GetProperties().ShouldBeEmpty(port.Name);
            port.GetEvents().ShouldBeEmpty(port.Name);
            port.GetInterfaces().ShouldBeEmpty(port.Name);
        }
    }

    [Fact]
    public void TitleReadPorts_ExposeNoMutationCapability()
    {
        var readers = ReadPortInventory.Keys.Concat(AggregatePortInventory.Keys);
        foreach (var method in readers.SelectMany(port => port.GetMethods()))
        {
            string name = $"{method.DeclaringType!.Name}.{method.Name}";
            method.Name.EndsWith("StagedAsync", StringComparison.Ordinal).ShouldBeFalse(name);
            WriteVerbs.Any(verb => method.Name.StartsWith(verb, StringComparison.Ordinal)).ShouldBeFalse(name);
            method.GetParameters().ShouldAllBe(parameter =>
                !ContainsType(parameter.ParameterType, typeof(Title))
                && !ContainsType(parameter.ParameterType, typeof(TitleMedia)), name);
        }

        foreach (var method in ReadPortInventory.Keys.SelectMany(port => port.GetMethods()))
            ContainsType(method.ReturnType, typeof(Title)).ShouldBeFalse($"{method.DeclaringType!.Name}.{method.Name}");
    }

    [Fact]
    public void TitlePorts_ExposeNoPersistenceTypes()
    {
        foreach (var method in AllPorts.SelectMany(port => port.Key.GetMethods()))
        {
            string name = $"{method.DeclaringType!.Name}.{method.Name}";
            foreach (var type in method.GetParameters().Select(parameter => parameter.ParameterType)
                         .Append(method.ReturnType)
                         .SelectMany(Flatten))
            {
                string ns = type.Namespace ?? string.Empty;
                ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal).ShouldBeFalse(name);
                ns.StartsWith("System.Linq.Expressions", StringComparison.Ordinal).ShouldBeFalse(name);
                ns.StartsWith("Romd.Persistence", StringComparison.Ordinal).ShouldBeFalse(name);
                typeof(IQueryable).IsAssignableFrom(type).ShouldBeFalse(name);
            }
        }
    }

    [Fact]
    public void TitlePortInventory_CoversEveryTitlePortInTheApplication()
    {
        // A new title port must be added to an inventory above, so it is pinned and checked too.
        var declared = typeof(ITitleReader).Assembly.GetTypes()
            .Where(type => type.IsInterface && type.Namespace == typeof(ITitleReader).Namespace)
            .Where(type => type.Name.StartsWith("ITitle", StringComparison.Ordinal)
                || type.Name.StartsWith("ITrackedTitle", StringComparison.Ordinal))
            .Except([typeof(ITrackedTitleRepository)])
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        declared.ShouldBe(AllPorts.Select(port => port.Key)
            .OrderBy(type => type.Name, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void BroadTitleRepository_DoesNotExist()
    {
        var assembly = typeof(ITitleReader).Assembly;
        assembly.GetType("Romd.Admin.Application.Titles.ITitleRepository").ShouldBeNull();
        assembly.GetTypes()
            .Where(type => type.IsInterface && type.Name.EndsWith("TitleRepository", StringComparison.Ordinal))
            .ShouldBe([typeof(ITrackedTitleRepository)]);
    }

    [Fact]
    public void AdminApplicationPorts_NoPortBothLoadsAndWritesTitleAggregates()
    {
        // A catch-all port would combine the aggregate load with title writes. Loading and each
        // write role stay on separate ports, even where one adapter implements several of them.
        var catchAll = typeof(ITitleReader).Assembly.GetTypes()
            .Where(type => type.IsInterface)
            .Where(type => type.GetMethods().Any(method => ContainsType(method.ReturnType, typeof(Title)))
                && type.GetMethods().Any(method => method.GetParameters()
                    .Any(parameter => ContainsType(parameter.ParameterType, typeof(Title)))))
            .ToArray();

        catchAll.ShouldBeEmpty();
    }

    [Fact]
    public void AdminApplication_OnlyTheAggregateReaderReturnsTitle()
    {
        // Every other member returns purpose-specific read models, so the only Title an application
        // write can receive is a complete aggregate (or a new Title.CreateNew for creation).
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var titleReturning = typeof(ITitleReader).Assembly.GetTypes()
            .Where(type => !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
            .SelectMany(type => type.GetMethods(Declared))
            .Where(method => !method.IsPrivate && !method.IsSpecialName && !method.IsAssembly)
            .Where(method => ContainsType(method.ReturnType, typeof(Title)))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();

        titleReturning.ShouldBe([$"{nameof(ITitleAggregateReader)}.{nameof(ITitleAggregateReader.GetAggregateAsync)}"]);
    }

    private static string Signature(MethodInfo method) =>
        $"{TypeName(method.ReturnType)} {method.Name}(" +
        string.Join(", ", method.GetParameters().Select(parameter => $"{TypeName(parameter.ParameterType)} {parameter.Name}")) +
        ")";

    private static string TypeName(Type type) =>
        type.IsGenericType
            ? $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>"
            : type.Name;

    private static bool ContainsType(Type candidate, Type target) => Flatten(candidate).Contains(target);

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;
        if (type.HasElementType)
        {
            foreach (var nested in Flatten(type.GetElementType()!))
                yield return nested;
        }

        foreach (var argument in type.IsGenericType ? type.GetGenericArguments() : [])
        {
            foreach (var nested in Flatten(argument))
                yield return nested;
        }
    }
}
