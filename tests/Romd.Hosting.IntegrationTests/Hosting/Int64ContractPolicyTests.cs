using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Common.Serialization;
using Romd.Contracts.Consumer.Releases;
using Romd.Contracts.Management.Artwork;
using Romd.Contracts.Management.Diagnostics;
using Romd.Contracts.Management.Models;
using Romd.Contracts.Management.Models.Media;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class Int64ContractPolicyTests
{
    private const long JavaScriptMaxSafeInteger = 9_007_199_254_740_991;
    private static readonly JsonSerializerOptions ContractJson = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, Int64Policy> ReviewedPolicies =
        new Dictionary<string, Int64Policy>(StringComparer.Ordinal)
        {
            [Key<StartMediaReconciliationSelectionRequest>(nameof(StartMediaReconciliationSelectionRequest.Limit))] = Number(
                "A finite work limit cannot exceed the three Int32 subject namespaces."),
            [Key<MediaReconciliationSelectionDto>(nameof(MediaReconciliationSelectionDto.Limit))] = Number(
                "The persisted finite work limit is at most three Int32 maxima."),
            [Key<MediaReconciliationSelectionDto>(nameof(MediaReconciliationSelectionDto.Selected))] = Number(
                "Captured high-watermarks bound every examined subject across three Int32 namespaces."),
            [Key<MediaReconciliationSelectionDto>(nameof(MediaReconciliationSelectionDto.Created))] = Number(
                "Created work is bounded by the finite work limit, below JavaScript's safe-integer ceiling."),
            [Key<MediaReconciliationSelectionDto>(nameof(MediaReconciliationSelectionDto.Adopted))] = Number(
                "Created plus adopted work cannot exceed the finite work limit."),
            [Key<MediaReconciliationSelectionDto>(nameof(MediaReconciliationSelectionDto.Skipped))] = Number(
                "Skipped subjects cannot exceed the three captured Int32 high-watermarks."),
            [Key<MediaReconciliationSelectionDto>(nameof(MediaReconciliationSelectionDto.Completed))] = Number(
                "Completed work cannot exceed bounded created plus adopted work."),
            [Key<MediaReconciliationSelectionDto>(nameof(MediaReconciliationSelectionDto.Failed))] = Number(
                "Failed work cannot exceed bounded completed work."),
            [Key<ArtworkSelectionStateDto>(nameof(ArtworkSelectionStateDto.Revision))] = String(
                "Persisted artwork selection revisions have no JavaScript-safe numeric bound."),
            [Key<WedgedReplaceDatJobDto>(nameof(WedgedReplaceDatJobDto.StalledForSeconds))] = Number(
                "DateTimeOffset-derived elapsed seconds are machine-bounded below JavaScript's safe-integer ceiling."),
            [Key<StrandedBulkEnrichmentJobDto>(nameof(StrandedBulkEnrichmentJobDto.StrandedForSeconds))] = Number(
                "DateTimeOffset-derived elapsed seconds are machine-bounded below JavaScript's safe-integer ceiling."),
            [Key<OutboxDiagnosticsDto>(nameof(OutboxDiagnosticsDto.OldestPendingAgeSeconds))] = Number(
                "DateTimeOffset-derived elapsed seconds are machine-bounded below JavaScript's safe-integer ceiling."),
            [Key<HangfireServerDiagnosticDto>(nameof(HangfireServerDiagnosticDto.HeartbeatAgeSeconds))] = Number(
                "DateTimeOffset-derived elapsed seconds are machine-bounded below JavaScript's safe-integer ceiling."),
            [Key<QueueBacklogDiagnosticDto>(nameof(QueueBacklogDiagnosticDto.EnqueuedCount))] = String(
                "Hangfire exposes an unconstrained Int64 queue count."),
            [Key<QueueBacklogDiagnosticDto>(nameof(QueueBacklogDiagnosticDto.FetchedCount))] = String(
                "Hangfire exposes an unconstrained Int64 queue count."),
            [Key<StorageDiagnosticsDto>(nameof(StorageDiagnosticsDto.DataVolumeFreeBytes))] = Bytes(
                "DriveInfo exposes an unconstrained Int64 byte count."),
            [Key<DatRom>(nameof(DatRom.Size))] = Bytes("A DAT-declared byte size has no JavaScript-safe bound."),
            [Key<TitleFileRequirement>(nameof(TitleFileRequirement.Size))] = Bytes(
                "A title requirement byte size has no JavaScript-safe bound."),
            [Key<Bios>(nameof(Bios.RequiredBytes))] = Bytes("A BIOS aggregate byte count has no JavaScript-safe bound."),
            [Key<Bios>(nameof(Bios.OwnedBytes))] = Bytes("A BIOS aggregate byte count has no JavaScript-safe bound."),
            [Key<Bios>(nameof(Bios.OnDiskBytes))] = Bytes("A BIOS aggregate byte count has no JavaScript-safe bound."),
            [Key<OriginalDiscLayoutDto>(nameof(OriginalDiscLayoutDto.DescriptorSizeBytes))] = Bytes("Original descriptor byte counts retain exact CAS identity."),
            [Key<OriginalDiscDependencyDto>(nameof(OriginalDiscDependencyDto.SizeBytes))] = Bytes("Exact dependency byte counts are never rounded."),
            [Key<OriginalDiscDependencyCandidateDto>(nameof(OriginalDiscDependencyCandidateDto.SizeBytes))] = Bytes("Original candidates use exact byte counts."),
            [Key<OriginalDiscRequirementDto>(nameof(OriginalDiscRequirementDto.SizeBytes))] = Bytes("Catalog original requirements use exact byte counts."),
            [Key<JobItemDto>(nameof(JobItemDto.SizeBytes))] = Bytes("An imported file byte size has no JavaScript-safe bound."),
            [Key<LibraryStats>(nameof(LibraryStats.TotalSizeBytes))] = Bytes("A library aggregate byte count has no JavaScript-safe bound."),
            [Key<LibraryStats>(nameof(LibraryStats.TotalSizeOnDiskBytes))] = Bytes("A library aggregate byte count has no JavaScript-safe bound."),
            [Key<LibraryStats>(nameof(LibraryStats.BytesSaved))] = Bytes("A library aggregate byte count has no JavaScript-safe bound."),
            [Key<Rom>(nameof(Rom.Size))] = Bytes("A ROM byte size has no JavaScript-safe bound."),
            [Key<StorageStatsDto>(nameof(StorageStatsDto.TotalStorageBytes))] = Bytes("A storage aggregate byte count has no JavaScript-safe bound."),
            [Key<StorageStatsDto>(nameof(StorageStatsDto.TotalStorageBytesOnDisk))] = Bytes("A storage aggregate byte count has no JavaScript-safe bound."),
            [Key<StorageStatsDto>(nameof(StorageStatsDto.BytesSaved))] = Bytes("A storage aggregate byte count has no JavaScript-safe bound."),
            [Key<StorageCategoryDto>(nameof(StorageCategoryDto.SizeBytes))] = Bytes("A storage category aggregate has no JavaScript-safe bound."),
            [Key<StorageCategoryDto>(nameof(StorageCategoryDto.SizeOnDiskBytes))] = Bytes("A storage category aggregate has no JavaScript-safe bound."),
            [Key<StoredFileRef>(nameof(StoredFileRef.SizeBytes))] = Bytes("A stored-file byte size has no JavaScript-safe bound."),
            [Key<StoredFileRef>(nameof(StoredFileRef.SizeOnDiskBytes))] = Bytes("A stored-file byte size has no JavaScript-safe bound."),
            [Key<ConsumerReleaseDto>(nameof(ConsumerReleaseDto.SizeBytes))] = Bytes("A release aggregate byte count has no JavaScript-safe bound."),
            [Key<ConsumerReleaseRuntimeDto>(nameof(ConsumerReleaseRuntimeDto.MinimumInstallBytes))] = Bytes("An install aggregate byte count has no JavaScript-safe bound."),
            [Key<ConsumerDiscSetFileDto>(nameof(ConsumerDiscSetFileDto.SizeBytes))] = Bytes("A bound disc companion byte count must remain exact."),
            [Key<ConsumerDerivedPlaylistDto>(nameof(ConsumerDerivedPlaylistDto.SizeBytes))] = Bytes("Derived playlist byte counts use the same exact byte-count wire policy."),
            [Key<ConsumerReleaseManifestItemDto>(nameof(ConsumerReleaseManifestItemDto.SizeBytes))] = Bytes("A manifest item byte size has no JavaScript-safe bound."),
            [Key<RomDeliveryFormatMetadataDto>(nameof(RomDeliveryFormatMetadataDto.SizeBytes))] = Bytes(
                "A delivered format byte size has no JavaScript-safe bound."),
            [Key<Romd.Contracts.Consumer.Delivery.ConsumerPlatformBiosItemDto>(nameof(Romd.Contracts.Consumer.Delivery.ConsumerPlatformBiosItemDto.SizeBytes))] = Bytes("A BIOS file byte size has no JavaScript-safe bound.")
        };

    [Fact]
    public void PublicContractInt64Fields_MatchTheReviewedFieldByFieldLedger()
    {
        PropertyInfo[] properties = ContractInt64Properties().ToArray();

        properties.Select(PropertyKey).ShouldBe(ReviewedPolicies.Keys, ignoreOrder: true);
        ReviewedPolicies.Values.ShouldAllBe(policy => !string.IsNullOrWhiteSpace(policy.Rationale));

        FindPolicyViolations(properties, ReviewedPolicies).ShouldBeEmpty();
    }

    [Fact]
    public void ReconciliationSelection_AtAllSubjectBounds_RetainsExactNumericWireValues()
    {
        long maximum = new Romd.Domain.Jobs.MediaReconciliationBounds(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue).MaximumSubjects;
        maximum.ShouldBe(8_589_934_588L);
        maximum.ShouldBeLessThan(JavaScriptMaxSafeInteger);
        var request = new StartMediaReconciliationSelectionRequest(Guid.NewGuid(), "AllSubjects", null, null, maximum, false);
        var selection = new MediaReconciliationSelectionDto
        {
            Filter = "AllSubjects",
            Limit = maximum,
            ConversionRequested = false,
            Selected = maximum,
            Created = maximum,
            Adopted = 0,
            Skipped = 0,
            Completed = maximum,
            Failed = 0,
            EnumerationComplete = true
        };
        var input = JsonSerializer.SerializeToElement(request, ContractJson).GetProperty("limit");
        input.ValueKind.ShouldBe(JsonValueKind.Number);
        input.GetInt64().ShouldBe(maximum);
        input.GetDouble().ShouldBe((double)maximum);
        var output = JsonSerializer.SerializeToElement(selection, ContractJson);
        foreach (string field in new[] { "limit", "selected", "created", "completed" })
        {
            output.GetProperty(field).ValueKind.ShouldBe(JsonValueKind.Number);
            output.GetProperty(field).GetInt64().ShouldBe(maximum);
            output.GetProperty(field).GetDouble().ShouldBe((double)maximum);
        }
    }

    [Fact]
    public void ArtworkSelectionRevision_AboveJavaScriptSafeRange_RetainsExactWireIdentity()
    {
        var state = new ArtworkSelectionStateDto("Poster", ArtworkMode.Automatic, long.MaxValue, null, null);
        var json = JsonSerializer.Serialize(state, ContractJson);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("revision").GetString().ShouldBe("9223372036854775807");
        JsonSerializer.Deserialize<ArtworkSelectionStateDto>(json, ContractJson).ShouldBe(state);
    }

    [Fact]
    public void PolicyScan_RejectsANewDifferentlyNamedRawInt64Field()
    {
        PropertyInfo[] properties = typeof(SyntheticFutureContract).GetProperties();

        FindPolicyViolations(properties, new Dictionary<string, Int64Policy>())
            .ShouldBe([
                $"{Key<SyntheticFutureContract>(nameof(SyntheticFutureContract.ArbitraryMagnitude))} is an unreviewed public Int64 contract field.",
                $"{Key<SyntheticFutureContract>(nameof(SyntheticFutureContract.UnsignedMagnitude))} is an unreviewed public Int64 contract field."
            ], ignoreOrder: true);
    }

    [Fact]
    public void PolicyScan_RejectsAFieldWhoseShapeDriftedFromItsReviewedPolicy()
    {
        PropertyInfo[] properties = typeof(SyntheticDriftedContract).GetProperties();
        var policies = new Dictionary<string, Int64Policy>(StringComparer.Ordinal)
        {
            [Key<SyntheticDriftedContract>(nameof(SyntheticDriftedContract.PayloadBytes))] = String(
                "Reviewed as a non-byte canonical string."),
            [Key<SyntheticDriftedContract>(nameof(SyntheticDriftedContract.QueueDepth))] = Bytes(
                "Reviewed as a byte count."),
            [Key<SyntheticDriftedContract>(nameof(SyntheticDriftedContract.ElapsedSeconds))] = Number(
                "Reviewed as a bounded JSON number.")
        };

        FindPolicyViolations(properties, policies).ShouldBe([
            $"{Key<SyntheticDriftedContract>(nameof(SyntheticDriftedContract.PayloadBytes))} is ByteCount but its reviewed wire policy is CanonicalDecimalString.",
            $"{Key<SyntheticDriftedContract>(nameof(SyntheticDriftedContract.QueueDepth))} is CanonicalDecimalString but its reviewed wire policy is ByteCount.",
            $"{Key<SyntheticDriftedContract>(nameof(SyntheticDriftedContract.ElapsedSeconds))} is ByteCount but its reviewed wire policy is JsonNumber."
        ], ignoreOrder: true);
    }

    [Fact]
    public void DateTimeOffsetElapsedSeconds_CannotExceedJavaScriptSafeInteger()
    {
        long completeDomainSeconds = (long)(DateTimeOffset.MaxValue - DateTimeOffset.MinValue)
            .TotalSeconds;

        completeDomainSeconds.ShouldBeLessThan(JavaScriptMaxSafeInteger);
    }

    [Theory]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("0", 0)]
    public void ByteCountConverter_RoundTripsExactString(string wireValue, long expected)
    {
        string json = $$"""{"category":"test","fileCount":0,"sizeBytes":"{{wireValue}}","sizeOnDiskBytes":"0"}""";

        var result = JsonSerializer.Deserialize<StorageCategoryDto>(json, ContractJson);

        result.ShouldNotBeNull();
        result.SizeBytes.ShouldBe((ByteCount)expected);
        JsonSerializer.Serialize(result, ContractJson).ShouldContain($$""""sizeBytes":"{{wireValue}}"""");
    }

    [Theory]
    [InlineData("123")]
    [InlineData("\"0123\"")]
    [InlineData("\"+123\"")]
    [InlineData("\"-0\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"-9223372036854775808\"")]
    public void ByteCountConverter_RejectsNonCanonicalOrNegativeWireShape(string sizeBytesJson)
    {
        string json = $$"""{"category":"test","fileCount":0,"sizeBytes":{{sizeBytesJson}},"sizeOnDiskBytes":"0"}""";

        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<StorageCategoryDto>(json, ContractJson));
    }

    [Fact]
    public void ByteCount_RejectsNegativeConstruction()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ByteCount(-1));
    }

    [Theory]
    [InlineData("null", null)]
    [InlineData("\"9007199254740993\"", 9_007_199_254_740_993)]
    public void ByteCountConverter_RoundTripsNullableFields(string wireValue, long? expected)
    {
        string json = $$"""{"value":{{wireValue}}}""";

        var result = JsonSerializer.Deserialize<SyntheticNullableByteCountContract>(json, ContractJson);

        result.ShouldNotBeNull();
        result.Value.ShouldBe((ByteCount?)expected);
        JsonSerializer.Serialize(result, ContractJson).ShouldBe(json);
    }

    [Fact]
    public void CanonicalInt64Converter_RoundTripsUInt64MaxAsAnExactString()
    {
        var value = new SyntheticUnsignedStringContract { Value = ulong.MaxValue };

        string json = JsonSerializer.Serialize(value, ContractJson);
        var roundTrip = JsonSerializer.Deserialize<SyntheticUnsignedStringContract>(json, ContractJson);

        json.ShouldBe("{\"value\":\"18446744073709551615\"}");
        roundTrip.ShouldBe(value);
    }

    private static IEnumerable<PropertyInfo> ContractInt64Properties() =>
        new[] { typeof(Rom).Assembly, typeof(ConsumerReleaseDto).Assembly }
            .SelectMany(assembly => assembly.GetExportedTypes())
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            .Where(property => Is64BitInteger(property.PropertyType));

    private static bool Is64BitInteger(Type type)
    {
        Type valueType = Nullable.GetUnderlyingType(type) ?? type;
        return valueType == typeof(long) || valueType == typeof(ulong) || valueType == typeof(ByteCount);
    }

    private static IReadOnlyList<string> FindPolicyViolations(
        IEnumerable<PropertyInfo> properties,
        IReadOnlyDictionary<string, Int64Policy> policies)
    {
        var violations = new List<string>();
        foreach (PropertyInfo property in properties)
        {
            string key = PropertyKey(property);
            if (!policies.TryGetValue(key, out Int64Policy? policy))
            {
                violations.Add($"{key} is an unreviewed public Int64 contract field.");
                continue;
            }

            JsonConverterAttribute? attribute = property.GetCustomAttribute<JsonConverterAttribute>();
            bool isByteCount =
                (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == typeof(ByteCount);
            Int64WireShape actualShape = isByteCount
                ? Int64WireShape.ByteCount
                : attribute?.ConverterType == typeof(CanonicalInt64StringJsonConverter)
                    ? Int64WireShape.CanonicalDecimalString
                    : Int64WireShape.JsonNumber;
            if (actualShape != policy.Shape)
                violations.Add($"{key} is {actualShape} but its reviewed wire policy is {policy.Shape}.");
        }

        return violations;
    }

    private static string Key<T>(string propertyName) => $"{typeof(T).FullName}.{propertyName}";
    private static string PropertyKey(PropertyInfo property) =>
        $"{property.DeclaringType!.FullName}.{property.Name}";
    private static Int64Policy Number(string rationale) => new(Int64WireShape.JsonNumber, rationale);
    private static Int64Policy String(string rationale) => new(Int64WireShape.CanonicalDecimalString, rationale);
    private static Int64Policy Bytes(string rationale) => new(Int64WireShape.ByteCount, rationale);

    private sealed record Int64Policy(Int64WireShape Shape, string Rationale);
    private enum Int64WireShape { JsonNumber, CanonicalDecimalString, ByteCount }

    private sealed record SyntheticFutureContract
    {
        public long ArbitraryMagnitude { get; init; }
        public ulong UnsignedMagnitude { get; init; }
    }

    private sealed record SyntheticUnsignedStringContract
    {
        [JsonConverter(typeof(CanonicalInt64StringJsonConverter))]
        public ulong Value { get; init; }
    }

    private sealed record SyntheticNullableByteCountContract
    {
        public ByteCount? Value { get; init; }
    }

    private sealed record SyntheticDriftedContract
    {
        public ByteCount PayloadBytes { get; init; }

        [JsonConverter(typeof(CanonicalInt64StringJsonConverter))]
        public long QueueDepth { get; init; }

        public ByteCount ElapsedSeconds { get; init; }
    }
}
