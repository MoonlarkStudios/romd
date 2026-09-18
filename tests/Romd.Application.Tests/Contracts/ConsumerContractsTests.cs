using System.Reflection;
using System.Runtime.CompilerServices;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Auth;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Contracts;

public class ConsumerContractsTests
{
    private static readonly string[] ForbiddenConsumerPropertyTerms =
    {
        "configuration",
        "crc",
        "curated",
        "curation",
        "datid",
        "datids",
        "dump",
        "excluded",
        "fileid",
        "hash",
        "materialization",
        "materialized",
        "md5",
        "provenance",
        "sha",
        "state"
    };

    private static readonly string[] ForbiddenConsumerReleaseVocabulary =
    {
        "LaunchOption",
        "InstallManifest",
        "ImmediateDownload",
        "SupportsImmediateDownload",
        "SupportsRangeRequests"
    };

    private static readonly HashSet<string> AllowedConsumerVerificationProperties =
    [
        "ConsumerReleaseManifestItemDto.Sha256",
        "ConsumerPlatformBiosItemDto.Md5",
        "ConsumerPlatformBiosItemDto.Sha1",
        "ConsumerPlatformBiosItemDto.Sha256"
    ];

    [Fact]
    public void ConsumerContracts_Phase0ADtoShape_MatchesSnapshot()
    {
        string snapshot = ReadSnapshot("consumer-contracts.txt");

        BuildConsumerContractSnapshot().ShouldBe(snapshot);
    }

    [Fact]
    public void ConsumerContracts_Phase0ADecisions_DoNotExposeForbiddenConsumerFields()
    {
        var violations = GetConsumerDtoTypes()
            .SelectMany(type => type.GetProperties().Select(property => new
            {
                Dto = type.Name,
                Property = property.Name,
                NormalizedProperty = property.Name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant()
            }))
            .SelectMany(candidate => ForbiddenConsumerPropertyTerms
                .Where(term => candidate.NormalizedProperty.Contains(term, StringComparison.Ordinal))
                .Where(_ => !AllowedConsumerVerificationProperties.Contains(
                    $"{candidate.Dto}.{candidate.Property}",
                    StringComparer.Ordinal))
                .Select(term => $"{candidate.Dto}.{candidate.Property} matched forbidden term '{term}'"))
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void ConsumerContracts_Phase0ADecisions_DoNotReferenceManagementOrServerTypes()
    {
        var violations = GetConsumerDtoTypes()
            .SelectMany(type => type
                .GetProperties()
                .SelectMany(property => GetReferencedTypes(property.PropertyType)
                    .Select(referencedType => new { Dto = type.Name, Property = property.Name, ReferencedType = referencedType })))
            .Where(candidate => IsForbiddenConsumerReference(candidate.ReferencedType))
            .Select(candidate => $"{candidate.Dto}.{candidate.Property}: {candidate.ReferencedType.FullName}")
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void ConsumerContracts_PhaseOnePointEight_DoNotExposeLaunchOptionVocabulary()
    {
        var violations = GetConsumerDtoTypes()
            .SelectMany(type => new[] { type.FullName! }
                .Concat(type.GetProperties().Select(property => $"{type.Name}.{property.Name}")))
            .SelectMany(name => ForbiddenConsumerReleaseVocabulary
                .Where(term => name.Contains(term, StringComparison.OrdinalIgnoreCase))
                .Select(term => $"{name} matched forbidden release-model term '{term}'"))
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void Page_IsOwnedByCommonContracts()
    {
        typeof(Page<>).Namespace.ShouldBe("Romd.Contracts.Common.Models");
    }

    private static string BuildConsumerContractSnapshot()
    {
        var lines = new List<string>
        {
            "# Consumer DTO snapshot from Phase 0A decisions.",
            "# Scope is implicit /api/me and /api/me/library library context.",
            "# Collections are consumer-read-only browse projections; titles expose owned Releases.",
            ""
        };

        foreach (Type type in GetConsumerDtoTypes())
        {
            lines.Add(type.FullName!);

            lines.AddRange(type
                .GetProperties()
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property =>
                {
                    string required = property.GetCustomAttribute<RequiredMemberAttribute>() is null
                        ? string.Empty
                        : " [required]";

                    return $"  {property.Name}: {FormatPropertyType(property)}{required}";
                }));

            lines.Add(string.Empty);
        }

        return string.Join('\n', lines).TrimEnd();
    }

    private static IReadOnlyList<Type> GetConsumerDtoTypes() =>
        typeof(CurrentUserDto)
            .Assembly
            .GetExportedTypes()
            .Where(type => type.Namespace?.StartsWith("Romd.Contracts.Consumer", StringComparison.Ordinal) is true)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

    private static string FormatPropertyType(PropertyInfo property)
    {
        string typeName = FormatType(property.PropertyType);
        bool isNullableReference = !property.PropertyType.IsValueType
            && new NullabilityInfoContext().Create(property).ReadState is NullabilityState.Nullable;

        return isNullableReference ? $"{typeName}?" : typeName;
    }

    private static string FormatType(Type type)
    {
        Type? nullableType = Nullable.GetUnderlyingType(type);
        if (nullableType is not null)
        {
            return $"{FormatType(nullableType)}?";
        }

        if (TypeAliases.TryGetValue(type, out string? alias))
        {
            return alias;
        }

        if (type.IsGenericType)
        {
            string typeName = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
            string genericArguments = string.Join(", ", type.GetGenericArguments().Select(FormatType));

            return $"{typeName}<{genericArguments}>";
        }

        return type.Name;
    }

    private static IEnumerable<Type> GetReferencedTypes(Type type)
    {
        yield return type;

        if (Nullable.GetUnderlyingType(type) is { } nullableType)
        {
            yield return nullableType;
        }

        foreach (Type genericArgument in type.GetGenericArguments())
        {
            foreach (Type referencedType in GetReferencedTypes(genericArgument))
            {
                yield return referencedType;
            }
        }
    }

    private static bool IsForbiddenConsumerReference(Type type) =>
        type.Namespace?.StartsWith("Romd.Contracts.Management", StringComparison.Ordinal) is true
        || type.Namespace?.StartsWith("Romd.Admin.Application", StringComparison.Ordinal) is true
        || type.Namespace?.StartsWith("Romd.Consumer.Application", StringComparison.Ordinal) is true
        || type.Namespace?.StartsWith("Romd.Application", StringComparison.Ordinal) is true
        || type.Namespace?.StartsWith("Romd.Domain", StringComparison.Ordinal) is true
        || type.Namespace?.StartsWith("Romd.Host", StringComparison.Ordinal) is true
        || type.Namespace?.StartsWith("Romd.Infrastructure", StringComparison.Ordinal) is true;

    private static string ReadSnapshot(string fileName, [CallerFilePath] string sourceFilePath = "")
    {
        string snapshotPath = Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "Snapshots", fileName);

        return File.ReadAllText(snapshotPath).ReplaceLineEndings("\n").TrimEnd();
    }

    private static readonly IReadOnlyDictionary<Type, string> TypeAliases = new Dictionary<Type, string>
    {
        [typeof(bool)] = "bool",
        [typeof(double)] = "double",
        [typeof(Guid)] = "Guid",
        [typeof(int)] = "int",
        [typeof(long)] = "long",
        [typeof(string)] = "string"
    };
}
