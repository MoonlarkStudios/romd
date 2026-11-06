using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class RomPs2DvdEligibilityResolverTests
{
    private const long LogicalBytes = 1 << 20;
    private static readonly Sha256 LogicalSha256 = Sha256.Parse(new string('a', 64));
    private static readonly Sha256 OtherSha256 = Sha256.Parse(new string('b', 64));
    private static readonly Sha256 RawReferenceSha256 = Sha256.Parse(new string('c', 64));
    private static readonly Sha1 LogicalSha1 = Sha1.Parse(new string('d', 40));
    private static readonly Md5 LogicalMd5 = Md5.Parse(new string('e', 32));
    private static readonly Crc32 LogicalCrc = Crc32.FromUInt32(42);
    private static readonly Guid ReferenceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherReferenceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(-5));

    [Theory]
    [InlineData(RomDiscReferenceMedia.Dvd5, RomDvdMediaKind.Dvd5)]
    [InlineData(RomDiscReferenceMedia.Dvd9, RomDvdMediaKind.Dvd9)]
    public async Task ResolveAsync_ExactPublishedDvdTuple_BindsMeasuredSha256AndRetainsSeparateProvenance(
        RomDiscReferenceMedia media, RomDvdMediaKind expectedKind)
    {
        var reference = Record(media: media);
        var store = new Store([reference]);
        var measured = Measurement();
        var boot = Boot();

        var result = await new RomPs2DvdEligibilityResolver(store).ResolveAsync(measured, boot, ObservedAt);

        result.IsError.ShouldBeFalse();
        result.Value.Status.ShouldBe(RomPs2DvdResolutionStatus.Supported);
        result.Value.Reference.ShouldBeSameAs(reference);
        store.Calls.ShouldBe(1);
        store.LastQuery.ShouldBe(new RomDiscMediaReferenceQuery(LogicalBytes, LogicalSha1, LogicalMd5, LogicalCrc));
        store.LastLimit.ShouldBe(RomPs2DvdEligibilityResolver.MaximumReferenceRecords + 1);
        var eligibility = result.Value.Eligibility.ShouldNotBeNull();
        eligibility.ContentSha256.ShouldBe(LogicalSha256);
        eligibility.ContentSha256.ShouldNotBe(reference.RawReferenceSha256);
        eligibility.ContentSize.ShouldBe(LogicalBytes);
        eligibility.Adapter.ShouldBe("ps2-iso");
        eligibility.Profile.ShouldBe(RomPs2DvdEligibilityFacts.ProfileVersion);
        eligibility.InspectorBuild.ShouldBe(boot.InspectorBuild);
        eligibility.DiscKind.ShouldBe(RomIsoDiscKind.PlayStation2Dvd);
        eligibility.Status.ShouldBe(RomIsoEligibilityStatus.Supported);
        eligibility.WiiPartitionCount.ShouldBe(0);
        eligibility.StandardKeyPartitions.ShouldBe(0);
        eligibility.KoreanKeyPartitions.ShouldBe(0);
        eligibility.InspectedAt.ShouldBe(ObservedAt.ToUniversalTime());
        eligibility.InspectedAt.Offset.ShouldBe(TimeSpan.Zero);
        var details = eligibility.Ps2DvdFacts.ShouldNotBeNull();
        details.Boot.ShouldBeSameAs(boot);
        details.ContentSha256.ShouldBe(measured.Sha256);
        details.ContentSize.ShouldBe(measured.Size);
        details.Sha1.ShouldBe(measured.Sha1);
        details.Md5.ShouldBe(measured.Md5);
        details.Crc32.ShouldBe(measured.Crc32);
        details.DvdReference.ReferenceRecordId.ShouldBe(reference.Id);
        details.DvdReference.MediaKind.ShouldBe(expectedKind);
        details.DvdReference.ContentSha256.ShouldBe(LogicalSha256);
        details.DvdReference.Sha1.ShouldBe(LogicalSha1);
        details.DvdReference.Md5.ShouldBe(LogicalMd5);
        details.DvdReference.Crc32.ShouldBe(LogicalCrc);
        details.DvdReference.ReferenceSha256.ShouldBe(reference.RawReferenceSha256);
        details.DvdReference.ReferenceSource.ShouldBe(reference.ReferenceSource);
        details.DvdReference.Profile.ShouldBe(reference.ParserProfile);
        reference.RawReferenceSha256.ShouldNotBe(LogicalSha256);
        reference.AcceptedSourceProvenance.ShouldBe("synthetic-local-import/v1");
        reference.RawReferenceBytes.ShouldBe(4096);
        reference.RetrievedAt.ShouldBe(ObservedAt.ToUniversalTime());
    }

    [Fact]
    public async Task ResolveAsync_SamePublishedQuartetDifferentMeasuredSha256_DoesNotDeriveFullIdentityFromReference()
    {
        var store = new Store([Record()]);
        var resolver = new RomPs2DvdEligibilityResolver(store);
        var first = await resolver.ResolveAsync(Measurement(), Boot(), ObservedAt);
        var second = await resolver.ResolveAsync(Measurement(content: OtherSha256), Boot(), ObservedAt);

        var firstEligibility = first.Value.Eligibility.ShouldNotBeNull();
        var secondEligibility = second.Value.Eligibility.ShouldNotBeNull();
        firstEligibility.ContentSha256.ShouldBe(LogicalSha256);
        secondEligibility.ContentSha256.ShouldBe(OtherSha256);
        firstEligibility.MatchesObservation(secondEligibility).ShouldBeFalse();
        firstEligibility.Ps2DvdFacts!.DvdReference.ReferenceSha256.ShouldBe(RawReferenceSha256);
        secondEligibility.Ps2DvdFacts!.DvdReference.ReferenceSha256.ShouldBe(RawReferenceSha256);
        store.Calls.ShouldBe(2);
    }

    [Theory]
    [InlineData("length", true)]
    [InlineData("length", false)]
    [InlineData("sha1", true)]
    [InlineData("sha1", false)]
    [InlineData("md5", true)]
    [InlineData("md5", false)]
    [InlineData("crc", true)]
    [InlineData("crc", false)]
    public async Task ResolveAsync_AnyReturnedTupleContradictsQuery_RejectsEvenBesideAnExactDvdMatch(string field, bool wrongFirst)
    {
        var wrong = Record(id: OtherReferenceId, bytes: field == "length" ? LogicalBytes + 2048 : LogicalBytes,
            sha1: field == "sha1" ? Sha1.Parse(new string('1', 40)) : LogicalSha1,
            md5: field == "md5" ? Md5.Parse(new string('2', 32)) : LogicalMd5,
            crc: field == "crc" ? Crc32.FromUInt32(43) : LogicalCrc);
        var right = Record();
        var store = new Store(wrongFirst ? [wrong, right] : [right, wrong]);

        var result = await new RomPs2DvdEligibilityResolver(store).ResolveAsync(Measurement(), Boot(), ObservedAt);

        result.FirstError.ShouldBe(RomDiscMediaReferenceErrors.TupleMismatch());
        store.LastQuery.ShouldBe(new RomDiscMediaReferenceQuery(LogicalBytes, LogicalSha1, LogicalMd5, LogicalCrc));
    }

    [Fact]
    public async Task ResolveAsync_MeasuredZeroCrc_IsCompleteAndMatchesPublishedZero()
    {
        var zero = Crc32.FromUInt32(0);
        var store = new Store([Record(crc: zero)]);

        var result = await new RomPs2DvdEligibilityResolver(store).ResolveAsync(Measurement(crc: zero), Boot(), ObservedAt);

        result.Value.Status.ShouldBe(RomPs2DvdResolutionStatus.Supported);
        result.Value.Eligibility!.Ps2DvdFacts!.Crc32.ShouldBe(zero);
        store.LastQuery!.Crc32.IsEmpty.ShouldBeFalse();
    }

    [Theory]
    [InlineData("zero-length")]
    [InlineData("unaligned")]
    [InlineData("oversized")]
    [InlineData("foreign-boot-length")]
    public async Task ResolveAsync_IncompleteOrForeignMeasurement_RejectsBeforeReferenceLookup(string field)
    {
        long bytes = field switch
        {
            "zero-length" => 0,
            "unaligned" => LogicalBytes + 1,
            "oversized" => RomPs2BootLayoutFacts.MaximumLogicalBytes + 2048,
            _ => LogicalBytes
        };
        var measured = Measurement(bytes: bytes);
        var store = new Store([Record()]);

        var result = await new RomPs2DvdEligibilityResolver(store).ResolveAsync(measured,
            Boot(field == "foreign-boot-length" ? LogicalBytes + 2048 : LogicalBytes), ObservedAt);

        result.FirstError.ShouldBe(RomDiscMediaReferenceErrors.InvalidMeasurement());
        store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ResolveAsync_NoPositiveBootFacts_RemainsUnsupportedWithoutCreatingCacheEvidenceOrLookingUpReference()
    {
        var store = new Store([Record()]);

        var result = await new RomPs2DvdEligibilityResolver(store).ResolveAsync(Measurement(), null, ObservedAt);

        result.Value.Status.ShouldBe(RomPs2DvdResolutionStatus.UnsupportedBootLayout);
        result.Value.Eligibility.ShouldBeNull();
        result.Value.Reference.ShouldBeNull();
        store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ResolveAsync_BootFactsBecomeAvailable_DoesNotCachePriorNoBootDecision()
    {
        var store = new Store([Record()]);
        var resolver = new RomPs2DvdEligibilityResolver(store);
        var measured = Measurement();
        var unsupported = await resolver.ResolveAsync(measured, null, ObservedAt);

        var resolved = await resolver.ResolveAsync(measured, Boot(), ObservedAt);

        unsupported.Value.Status.ShouldBe(RomPs2DvdResolutionStatus.UnsupportedBootLayout);
        unsupported.Value.Eligibility.ShouldBeNull();
        resolved.Value.Status.ShouldBe(RomPs2DvdResolutionStatus.Supported);
        resolved.Value.Eligibility!.ContentSha256.ShouldBe(measured.Sha256);
        store.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData("missing", RomPs2DvdResolutionStatus.PendingReference)]
    [InlineData("unavailable", RomPs2DvdResolutionStatus.PendingReferenceUnavailable)]
    [InlineData("conflict", RomPs2DvdResolutionStatus.PendingConflictingReferences)]
    [InlineData("cd", RomPs2DvdResolutionStatus.UnsupportedMedia)]
    [InlineData("ps1", RomPs2DvdResolutionStatus.UnsupportedSystem)]
    [InlineData("multifile", RomPs2DvdResolutionStatus.UnsupportedFileMultiplicity)]
    public async Task ResolveAsync_ReferenceImportAfterUnresolvedResult_ReevaluatesIdenticalMeasurementProfileAndBuild(
        string initial, RomPs2DvdResolutionStatus expected)
    {
        var store = new Store(initial switch
        {
            "conflict" => [Record(), Record(id: OtherReferenceId, media: RomDiscReferenceMedia.Cd)],
            "cd" => [Record(media: RomDiscReferenceMedia.Cd)],
            "ps1" => [Record(system: RomDiscReferenceSystem.PlayStation1)],
            "multifile" => [Record(fileCount: 2)],
            _ => []
        });
        if (initial == "unavailable")
            store.Response = RomDiscMediaReferenceErrors.Unavailable();
        var resolver = new RomPs2DvdEligibilityResolver(store);
        var measured = Measurement();
        var boot = Boot();

        var pending = await resolver.ResolveAsync(measured, boot, ObservedAt);
        store.SetRows([Record()]);
        var resolved = await resolver.ResolveAsync(measured, boot, ObservedAt);

        pending.Value.Status.ShouldBe(expected);
        pending.Value.Eligibility.ShouldBeNull();
        resolved.Value.Status.ShouldBe(RomPs2DvdResolutionStatus.Supported);
        var eligibility = resolved.Value.Eligibility.ShouldNotBeNull();
        eligibility.InspectorBuild.ShouldBe(boot.InspectorBuild);
        eligibility.Profile.ShouldBe(RomPs2DvdEligibilityFacts.ProfileVersion);
        eligibility.ContentSha256.ShouldBe(measured.Sha256);
        store.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task ResolveAsync_ReferenceConflictsAfterPositiveResolution_DoesNotReuseOldPositiveEvidence()
    {
        var store = new Store([Record()]);
        var resolver = new RomPs2DvdEligibilityResolver(store);
        var measured = Measurement();
        var boot = Boot();
        var first = await resolver.ResolveAsync(measured, boot, ObservedAt);
        store.SetRows([Record(), Record(id: OtherReferenceId, media: RomDiscReferenceMedia.Dvd9)]);

        var current = await resolver.ResolveAsync(measured, boot, ObservedAt);

        first.Value.Eligibility.ShouldNotBeNull();
        current.Value.Status.ShouldBe(RomPs2DvdResolutionStatus.PendingConflictingReferences);
        current.Value.Eligibility.ShouldBeNull();
        current.Value.Reference.ShouldBeNull();
        store.Calls.ShouldBe(2);
    }

    [Theory]
    [InlineData("cd", true)]
    [InlineData("cd", false)]
    [InlineData("dvd9", true)]
    [InlineData("dvd9", false)]
    [InlineData("ps1", true)]
    [InlineData("ps1", false)]
    [InlineData("multifile", true)]
    [InlineData("multifile", false)]
    public async Task ResolveAsync_ExactMatchingConflictingMediaSystemOrMultiplicity_FailsClosedInEitherOrder(string changed, bool changedFirst)
    {
        var other = Record(id: OtherReferenceId,
            system: changed == "ps1" ? RomDiscReferenceSystem.PlayStation1 : RomDiscReferenceSystem.PlayStation2,
            media: changed switch { "cd" => RomDiscReferenceMedia.Cd, "dvd9" => RomDiscReferenceMedia.Dvd9, _ => RomDiscReferenceMedia.Dvd5 },
            fileCount: changed == "multifile" ? 2 : 1);
        var right = Record();
        var store = new Store(changedFirst ? [other, right] : [right, other]);

        var result = await new RomPs2DvdEligibilityResolver(store).ResolveAsync(Measurement(), Boot(), ObservedAt);

        result.Value.Status.ShouldBe(RomPs2DvdResolutionStatus.PendingConflictingReferences);
        result.Value.Eligibility.ShouldBeNull();
        result.Value.Reference.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_EquivalentDuplicateReferences_RetainsDeterministicExactRawProvenance()
    {
        var selected = Record(rawHash: OtherSha256, source: "synthetic-reference/Z", profile: "reference-parser/v2",
            provenance: "trusted-source-pin/v2", rawBytes: 8192);
        var other = Record(id: OtherReferenceId);
        var store = new Store([other, selected]);
        var resolver = new RomPs2DvdEligibilityResolver(store);
        var first = await resolver.ResolveAsync(Measurement(), Boot(), ObservedAt);
        store.SetRows([selected, other, selected]);

        var reversed = await resolver.ResolveAsync(Measurement(), Boot(), ObservedAt);

        first.Value.Reference.ShouldBeSameAs(selected);
        reversed.Value.Reference.ShouldBeSameAs(selected);
        var retainedReference = reversed.Value.Reference.ShouldNotBeNull();
        var eligibility = reversed.Value.Eligibility.ShouldNotBeNull();
        var details = eligibility.Ps2DvdFacts.ShouldNotBeNull();
        retainedReference.Id.ShouldBe(ReferenceId);
        retainedReference.AcceptedSourceProvenance.ShouldBe("trusted-source-pin/v2");
        retainedReference.RawReferenceBytes.ShouldBe(8192);
        details.DvdReference.ReferenceRecordId.ShouldBe(selected.Id);
        details.DvdReference.ReferenceSha256.ShouldBe(OtherSha256);
        details.DvdReference.Profile.ShouldBe("reference-parser/v2");
        details.DvdReference.ReferenceSource.ShouldBe("synthetic-reference/Z");
        first.Value.Eligibility!.MatchesObservation(eligibility).ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_IdenticalFactsDifferentRetrievalIds_BindsExactSelectionWithoutRelabelingEvidence()
    {
        var firstRecord = Record();
        var secondRecord = Record(id: OtherReferenceId);
        var store = new Store([secondRecord, firstRecord]);
        var resolver = new RomPs2DvdEligibilityResolver(store);
        var first = (await resolver.ResolveAsync(Measurement(), Boot(), ObservedAt)).Value;
        first.Reference!.Id.ShouldBe(ReferenceId);
        first.Eligibility!.Ps2DvdFacts!.DvdReference.ReferenceRecordId.ShouldBe(ReferenceId);
        store.SetRows([secondRecord]);
        var second = (await resolver.ResolveAsync(Measurement(), Boot(), ObservedAt)).Value;
        second.Reference!.Id.ShouldBe(OtherReferenceId);
        second.Eligibility!.Ps2DvdFacts!.DvdReference.ReferenceRecordId.ShouldBe(OtherReferenceId);
        second.Eligibility.ShouldNotBeNull().MatchesObservation(first.Eligibility.ShouldNotBeNull()).ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_DuplicateRawHashesWithDifferentSources_UsesOrdinalProvenanceSelection()
    {
        var uppercase = Record(source: "Z", id: OtherReferenceId);
        var lowercase = Record(source: "a");
        var store = new Store([lowercase, uppercase]);
        var resolver = new RomPs2DvdEligibilityResolver(store);

        var first = await resolver.ResolveAsync(Measurement(), Boot(), ObservedAt);
        store.SetRows([uppercase, lowercase]);
        var second = await resolver.ResolveAsync(Measurement(), Boot(), ObservedAt);

        first.Value.Reference.ShouldBeSameAs(uppercase);
        second.Value.Reference.ShouldBeSameAs(uppercase);
        second.Value.Eligibility!.Ps2DvdFacts!.DvdReference.ReferenceSource.ShouldBe("Z");
    }

    [Theory]
    [InlineData("raw-hash")]
    [InlineData("raw-size")]
    [InlineData("source")]
    [InlineData("profile")]
    [InlineData("provenance")]
    [InlineData("time")]
    public async Task ResolveAsync_RepeatedRecordIdWithDifferentRetainedFacts_RejectsStoreIdentityContradiction(string changed)
    {
        var different = Record(rawHash: changed == "raw-hash" ? OtherSha256 : RawReferenceSha256,
            rawBytes: changed == "raw-size" ? 8192 : 4096,
            source: changed == "source" ? "other-source" : "synthetic-official-record",
            profile: changed == "profile" ? "reference-parser/v2" : "reference-parser/v1",
            provenance: changed == "provenance" ? "other-local-import/v1" : "synthetic-local-import/v1",
            retrievedAt: changed == "time" ? ObservedAt.AddTicks(1) : ObservedAt);

        var result = await new RomPs2DvdEligibilityResolver(new Store([Record(), different]))
            .ResolveAsync(Measurement(), Boot(), ObservedAt);

        result.FirstError.ShouldBe(RomDiscMediaReferenceErrors.InvalidStoreResult());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveAsync_NullStoreListOrEntry_RejectsUntrustedResult(bool nullEntry)
    {
        var store = new Store([]);
        store.SetRows(nullEntry ? [null!] : null!);

        var result = await new RomPs2DvdEligibilityResolver(store).ResolveAsync(Measurement(), Boot(), ObservedAt);

        result.FirstError.ShouldBe(RomDiscMediaReferenceErrors.InvalidStoreResult());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveAsync_ReferenceCountBoundary_RejectsOverflowWithoutTruncating(bool overflow)
    {
        var records = Enumerable.Repeat(Record(), RomPs2DvdEligibilityResolver.MaximumReferenceRecords + (overflow ? 1 : 0)).ToArray();
        var store = new Store(records);

        var result = await new RomPs2DvdEligibilityResolver(store).ResolveAsync(Measurement(), Boot(), ObservedAt);

        if (overflow)
            result.FirstError.ShouldBe(RomDiscMediaReferenceErrors.ReferenceLimit());
        else
            result.Value.Status.ShouldBe(RomPs2DvdResolutionStatus.Supported);
        store.LastLimit.ShouldBe(RomPs2DvdEligibilityResolver.MaximumReferenceRecords + 1);
    }

    [Theory]
    [InlineData("validation")]
    [InlineData("other-failure")]
    [InlineData("forged-unavailable")]
    public async Task ResolveAsync_UnrelatedStoreError_PreservesExactBusinessFailure(string kind)
    {
        var error = kind switch
        {
            "other-failure" => Error.Failure("Reference.OtherFailure", "Different failure"),
            "forged-unavailable" => Error.Validation(RomDiscMediaReferenceErrors.UnavailableCode, "Invalid assertions"),
            _ => Error.Validation("Reference.InvalidImport", "Invalid import")
        };
        var store = new Store([]) { Response = error };

        var result = await new RomPs2DvdEligibilityResolver(store).ResolveAsync(Measurement(), Boot(), ObservedAt);

        result.Errors.ShouldBe(new[] { error });
    }

    [Fact]
    public async Task ResolveAsync_UnavailableAlongsideInvalidImport_DoesNotHideTheOtherFailure()
    {
        var errors = new List<Error> { RomDiscMediaReferenceErrors.Unavailable(), Error.Validation("Reference.InvalidImport", "Invalid import") };
        var store = new Store([]) { Response = errors };

        var result = await new RomPs2DvdEligibilityResolver(store).ResolveAsync(Measurement(), Boot(), ObservedAt);

        result.Errors.ShouldBe(errors);
    }

    [Fact]
    public async Task ResolveAsync_UnexpectedStoreException_DoesNotSwallowItAsMissingReference()
    {
        var failure = new InvalidOperationException("Unexpected local storage failure");
        var store = new Store([]) { Callback = (_, _, _) => throw failure };

        var actual = await Should.ThrowAsync<InvalidOperationException>(() =>
            new RomPs2DvdEligibilityResolver(store).ResolveAsync(Measurement(), Boot(), ObservedAt));

        actual.ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task ResolveAsync_PreCancelledCaller_DoesNotConsultStore()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new Store([Record()]);

        await AssertCancelledAsync(() => new RomPs2DvdEligibilityResolver(store)
            .ResolveAsync(Measurement(), Boot(), ObservedAt, cancellation.Token), cancellation.Token);

        store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ResolveAsync_CallerCancelsDuringLocalLookup_ForwardsAndHonorsExactToken()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store([Record()]);
        store.Callback = async (_, _, ct) => { entered.SetResult(); await release.Task.WaitAsync(ct); return store.Response; };
        var resolving = new RomPs2DvdEligibilityResolver(store).ResolveAsync(Measurement(), Boot(), ObservedAt, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        try
        { await AssertCancelledAsync(() => resolving.WaitAsync(TimeSpan.FromSeconds(5)), cancellation.Token); }
        finally { release.TrySetResult(); }

        store.Calls.ShouldBe(1);
        store.LastToken.ShouldBe(cancellation.Token);
    }

    [Fact]
    public async Task ResolveAsync_StoreReturnsAfterCancellation_DoesNotConvertItToPending()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new Store([]);
        store.Callback = (_, _, _) => { cancellation.Cancel(); return Task.FromResult(store.Response); };

        await AssertCancelledAsync(() => new RomPs2DvdEligibilityResolver(store)
            .ResolveAsync(Measurement(), Boot(), ObservedAt, cancellation.Token), cancellation.Token);

        store.LastToken.ShouldBe(cancellation.Token);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("system")]
    [InlineData("media")]
    [InlineData("files-zero")]
    [InlineData("files-limit")]
    [InlineData("logical-zero")]
    [InlineData("logical-limit")]
    [InlineData("sha1")]
    [InlineData("md5")]
    [InlineData("crc")]
    [InlineData("raw-zero")]
    [InlineData("raw-limit")]
    [InlineData("raw-hash")]
    [InlineData("source-empty")]
    [InlineData("source-limit")]
    [InlineData("source-control")]
    [InlineData("profile-empty")]
    [InlineData("profile-limit")]
    [InlineData("profile-control")]
    [InlineData("provenance-empty")]
    [InlineData("provenance-limit")]
    [InlineData("provenance-control")]
    public void Record_IncompleteUnknownOrOversizedAssertions_RejectsBeforeLocalImport(string changed) =>
        Should.Throw<ArgumentException>(() => Record(id: changed == "id" ? Guid.Empty : ReferenceId,
            system: changed == "system" ? (RomDiscReferenceSystem)99 : RomDiscReferenceSystem.PlayStation2,
            media: changed == "media" ? (RomDiscReferenceMedia)99 : RomDiscReferenceMedia.Dvd5,
            fileCount: changed switch { "files-zero" => 0, "files-limit" => RomDiscMediaReferenceRecord.MaximumFileCount + 1, _ => 1 },
            bytes: changed switch { "logical-zero" => 0, "logical-limit" => RomPs2BootLayoutFacts.MaximumLogicalBytes + 1, _ => LogicalBytes },
            sha1: changed == "sha1" ? default(Sha1) : LogicalSha1,
            md5: changed == "md5" ? default(Md5) : LogicalMd5,
            crc: changed == "crc" ? default(Crc32) : LogicalCrc,
            rawBytes: changed switch { "raw-zero" => 0, "raw-limit" => RomDiscMediaReferenceRecord.MaximumRawReferenceBytes + 1, _ => 4096 },
            rawHash: changed == "raw-hash" ? default(Sha256) : RawReferenceSha256,
            source: changed switch { "source-empty" => " ", "source-limit" => new string('s', 257), "source-control" => "source\n", _ => "synthetic-official-record" },
            profile: changed switch { "profile-empty" => " ", "profile-limit" => new string('p', 129), "profile-control" => "profile\0", _ => "reference-parser/v1" },
            provenance: changed switch { "provenance-empty" => " ", "provenance-limit" => new string('p', 257), "provenance-control" => "provenance\u007f", _ => "synthetic-local-import/v1" }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveAsync_AgreeingNewImport_RecomputesCurrentSelectionAndAuditTimeWithoutChangingRawFacts(bool reverse)
    {
        var original = Record();
        var earlier = Record(id: OtherReferenceId, rawHash: OtherSha256);
        var store = new Store([original]);
        var resolver = new RomPs2DvdEligibilityResolver(store);
        var identity = Measurement();
        var boot = Boot();
        var first = (await resolver.ResolveAsync(identity, boot, ObservedAt)).Value;
        store.SetRows(reverse ? [earlier, original] : [original, earlier]);
        var second = (await resolver.ResolveAsync(identity, boot, ObservedAt.AddTicks(7))).Value;
        first.Reference.ShouldBeSameAs(original);
        first.Eligibility!.InspectedAt.ShouldBe(ObservedAt.ToUniversalTime());
        second.Reference.ShouldBeSameAs(earlier);
        second.Eligibility!.InspectedAt.ShouldBe(ObservedAt.AddTicks(7).ToUniversalTime());
        first.Eligibility.Ps2DvdFacts!.Boot.ShouldBeSameAs(boot);
        second.Eligibility.Ps2DvdFacts!.Boot.ShouldBeSameAs(boot);
        identity.ShouldBe(Measurement());
        store.Calls.ShouldBe(2);
    }

    private static RomLogicalIdentity Measurement(long bytes = LogicalBytes, Crc32? crc = null, Sha256? content = null) =>
        new(bytes, content ?? LogicalSha256, LogicalSha1, LogicalMd5, crc ?? LogicalCrc);

    private static RomPs2BootLayoutFacts Boot(long bytes = LogicalBytes) =>
        new(bytes, 512, 16, 20, 2048, 21, 64, 22, 4096,
            "cdrom0:\\SLUS_123.45;1", "cdrom0:\\SLUS_123.45;1", "SLUS_123.45", 0x1000, 1,
            RomPs2BootLayoutFacts.ProfileVersion, "boot-inspector/synthetic");

    private static RomDiscMediaReferenceRecord Record(Guid? id = null,
        RomDiscReferenceSystem system = RomDiscReferenceSystem.PlayStation2, RomDiscReferenceMedia media = RomDiscReferenceMedia.Dvd5,
        int fileCount = 1, long bytes = LogicalBytes, Sha1? sha1 = null, Md5? md5 = null, Crc32? crc = null,
        long rawBytes = 4096, Sha256? rawHash = null, string source = "synthetic-official-record",
        string profile = "reference-parser/v1", string provenance = "synthetic-local-import/v1", DateTimeOffset? retrievedAt = null) =>
        new(id ?? ReferenceId, system, media, fileCount, bytes, sha1 ?? LogicalSha1, md5 ?? LogicalMd5, crc ?? LogicalCrc,
            rawBytes, rawHash ?? RawReferenceSha256, source, profile, provenance, retrievedAt ?? ObservedAt);

    private static async Task AssertCancelledAsync(Func<Task> operation, CancellationToken expected)
    {
        try
        { await operation(); }
        catch (OperationCanceledException exception) { exception.CancellationToken.ShouldBe(expected); return; }
        throw new InvalidOperationException("Expected caller cancellation to propagate.");
    }

    private sealed class Store : IRomDiscMediaReferenceStore
    {
        public Store(IReadOnlyList<RomDiscMediaReferenceRecord> records) => SetRows(records);
        public ErrorOr<IReadOnlyList<RomDiscMediaReferenceRecord>> Response { get; set; }
        public Func<RomDiscMediaReferenceQuery, int, CancellationToken, Task<ErrorOr<IReadOnlyList<RomDiscMediaReferenceRecord>>>>? Callback { get; set; }
        public int Calls { get; private set; }
        public RomDiscMediaReferenceQuery? LastQuery { get; private set; }
        public int LastLimit { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public void SetRows(IReadOnlyList<RomDiscMediaReferenceRecord> records) => Response = ErrorOrFactory.From(records);
        public Task<ErrorOr<IReadOnlyList<RomDiscMediaReferenceRecord>>> FindAsync(RomDiscMediaReferenceQuery query, int maximumResults, CancellationToken ct = default)
        {
            Calls++;
            LastQuery = query;
            LastLimit = maximumResults;
            LastToken = ct;
            return Callback?.Invoke(query, maximumResults, ct) ?? Task.FromResult(Response);
        }
    }
}
