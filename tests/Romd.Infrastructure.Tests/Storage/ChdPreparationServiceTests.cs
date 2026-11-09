using System.IO.Abstractions;
using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Hashing;
using Romd.Infrastructure.Storage;
using Romd.Media.Chd;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ChdPreparationServiceTests
{
    private static readonly byte[] Magic = "MComprHD"u8.ToArray();

    [Theory]
    [InlineData("disc.CHD", false)]
    [InlineData("disc.iso", false)]
    [InlineData("disc.bin", false)]
    [InlineData("disc.rvz", false)]
    [InlineData("disc.CHD", true)]
    [InlineData("disc.iso", true)]
    [InlineData("disc.bin", true)]
    [InlineData("disc.rvz", true)]
    public async Task PrepareAsync_FullChdMagicWithAnyName_ResolvesExactReferenceAfterOwnedAdmissionReleased(string filename, bool admitted)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        using var verifier = fixture.Verifier(supervisor);
        var store = fixture.Store(supervisor, [Reference()]);
        var service = fixture.Service(verifier, store);
        service.RequiresDatHashes(Magic, filename).ShouldBeFalse();

        var result = await PrepareAsync(service, source, filename, admitted);

        result.IsError.ShouldBeFalse();
        await using var prepared = result.Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();
        AssertImportedFacts(prepared, source);
        prepared.LogicalFilename.ShouldBe("disc.iso");
        var eligibility = prepared.Eligibility.ShouldNotBeNull();
        eligibility.Status.ShouldBe(RomIsoEligibilityStatus.Supported);
        eligibility.DiscKind.ShouldBe(RomIsoDiscKind.PlayStation2Dvd);
        eligibility.InspectedAt.ShouldBe(prepared.Verification.VerifiedAt);
        var details = eligibility.Ps2DvdFacts.ShouldNotBeNull();
        details.Boot.ShouldBeSameAs(prepared.ChdPs2Boot);
        details.DvdReference.ReferenceRecordId.ShouldBe(store.Response.Value[0].Id);
        details.DvdReference.ReferenceSha256.ShouldBe(store.Response.Value[0].RawReferenceSha256);
        details.DvdReference.ReferenceSource.ShouldBe(store.Response.Value[0].ReferenceSource);
        details.DvdReference.Profile.ShouldBe(store.Response.Value[0].ParserProfile);
        details.DvdReference.MediaKind.ShouldBe(RomDvdMediaKind.Dvd5);
        store.LastQuery.ShouldBe(new RomDiscMediaReferenceQuery(prepared.Hashes.BytesRead, prepared.Hashes.Sha1,
            prepared.Hashes.Md5, prepared.Hashes.Crc32));
        store.LastLimit.ShouldBe(RomPs2DvdEligibilityResolver.MaximumReferenceRecords + 1);
        store.Calls.ShouldBe(1);
        supervisor.Calls.ShouldBe(1);
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeTrue();
        source.OpenReads.ShouldBe(admitted ? 0 : 2);
        source.ReadBytes.ShouldBe(admitted ? 0 : 248);
        await prepared.DisposeAsync();
        source.DisposeCalls.ShouldBe(0);
        source.QuarantineCalls.ShouldBe(0);
        fixture.Hashing.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("unavailable", false)]
    [InlineData("unavailable", true)]
    [InlineData("conflict", false)]
    [InlineData("conflict", true)]
    [InlineData("cd", false)]
    [InlineData("system", true)]
    [InlineData("multiple", false)]
    public async Task PrepareAsync_UnresolvedReference_PreservesChdFactsAndRetriesWithoutNegativeEligibility(string reason, bool admitted)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        using var verifier = fixture.Verifier(supervisor);
        var rows = reason switch
        {
            "conflict" => new[] { Reference(), Reference(media: RomDiscReferenceMedia.Dvd9) },
            "cd" => new[] { Reference(media: RomDiscReferenceMedia.Cd) },
            "system" => new[] { Reference(system: RomDiscReferenceSystem.PlayStation1) },
            "multiple" => new[] { Reference(fileCount: 2) },
            _ => Array.Empty<RomDiscMediaReferenceRecord>()
        };
        var store = fixture.Store(supervisor, rows);
        if (reason == "unavailable")
            store.Response = RomDiscMediaReferenceErrors.Unavailable();
        var service = fixture.Service(verifier, store);

        var first = await PrepareAsync(service, source, "disc.chd", admitted);
        first.IsError.ShouldBeFalse();
        await using var pending = first.Value.ShouldBeOfType<PreparedRom.WholeFile>();
        AssertImportedFacts(pending, source);
        pending.Eligibility.ShouldBeNull();
        store.SetRows([Reference()]);
        var second = await PrepareAsync(service, source, "disc.chd", admitted);
        second.IsError.ShouldBeFalse();
        await using var resolved = second.Value.ShouldBeOfType<PreparedRom.WholeFile>();

        AssertImportedFacts(resolved, source);
        resolved.Eligibility.ShouldNotBeNull().Status.ShouldBe(RomIsoEligibilityStatus.Supported);
        resolved.ContentSha256.ShouldBe(pending.ContentSha256);
        resolved.Hashes.ShouldBe(pending.Hashes);
        pending.Eligibility.ShouldBeNull();
        store.Calls.ShouldBe(2);
        supervisor.Calls.ShouldBe(2);
        source.DisposeCalls.ShouldBe(0);
        source.QuarantineCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_WithoutBoot_PreservesIntegrityWithoutConsultingReferencesOrInventingObservation(bool admitted)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source, boot: false);
        using var verifier = fixture.Verifier(supervisor);
        var store = fixture.Store(supervisor, []);
        var service = fixture.Service(verifier, store);

        var result = await PrepareAsync(service, source, "disc.chd", admitted);

        result.IsError.ShouldBeFalse();
        await using var prepared = result.Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();
        prepared.Encoding.ShouldBe(RomRepresentationEncoding.Chd);
        prepared.ChdIntegrity.ShouldNotBeNull().MatchesMeasurement(prepared.Verification).ShouldBeTrue();
        prepared.ChdPs2Boot.ShouldBeNull();
        prepared.Ps2Observation.ShouldBeNull();
        prepared.Eligibility.ShouldBeNull();
        prepared.ChdEncodingEvidence.ShouldBeNull();
        prepared.SourceEquivalence.ShouldBeNull();
        store.Calls.ShouldBe(0);
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeTrue();
    }

    [Theory]
    [InlineData("error", false)]
    [InlineData("error", true)]
    [InlineData("tuple", false)]
    [InlineData("tuple", true)]
    [InlineData("cancel", false)]
    [InlineData("cancel", true)]
    [InlineData("throw", false)]
    [InlineData("throw", true)]
    public async Task PrepareAsync_ReferenceErrorOrCancellation_PreservesBorrowedSourceAndStoppedAdmission(string fault, bool admitted)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        using var verifier = fixture.Verifier(supervisor);
        using var cancellation = new CancellationTokenSource();
        var store = fixture.Store(supervisor, [Reference(sha1: fault == "tuple" ? new string('e', 40) : null)]);
        var expected = Error.Validation("Reference.InvalidImport", "Invalid trusted import.");
        if (fault == "error")
            store.Response = expected;
        if (fault == "cancel")
            store.OnFind = () => cancellation.Cancel();
        if (fault == "throw")
            store.OnFind = () => throw new IOException("Reference storage failed.");
        var service = fixture.Service(verifier, store);

        if (fault == "cancel")
        {
            OperationCanceledException? error = null;
            try
            { await PrepareAsync(service, source, "disc.chd", admitted, cancellation.Token); }
            catch (OperationCanceledException exception) { error = exception; }
            error.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
            store.LastToken.ShouldBe(cancellation.Token);
        }
        else if (fault == "throw")
            (await Should.ThrowAsync<IOException>(() => PrepareAsync(service, source, "disc.chd", admitted))).Message.ShouldBe("Reference storage failed.");
        else
        {
            var result = await PrepareAsync(service, source, "disc.chd", admitted);
            result.Errors.ShouldBe(new[] { fault == "tuple" ? RomDiscMediaReferenceErrors.TupleMismatch() : expected });
        }

        store.Calls.ShouldBe(1);
        source.DisposeCalls.ShouldBe(0);
        source.QuarantineCalls.ShouldBe(0);
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeTrue();
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Theory]
    [InlineData(MediaWorkerFailure.Malformed, false)]
    [InlineData(MediaWorkerFailure.Protocol, true)]
    [InlineData(MediaWorkerFailure.Unsupported, false)]
    public async Task PrepareAsync_StoppedInspectionFailure_DoesNotResolveReferencesOrInventNegativeFacts(MediaWorkerFailure failure, bool admitted)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        supervisor.Failure = failure;
        using var verifier = fixture.Verifier(supervisor);
        var store = fixture.Store(supervisor, [Reference()]);

        var result = await PrepareAsync(fixture.Service(verifier, store), source, "disc.chd", admitted);

        result.FirstError.Code.ShouldBe("Rom.Media." + failure);
        store.Calls.ShouldBe(0);
        source.QuarantineCalls.ShouldBe(0);
        source.DisposeCalls.ShouldBe(0);
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeTrue();
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_CertifiedStoppedChildCancellation_PropagatesCallerTokenWithoutResolvingOrQuarantining(bool admitted)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        using var verifier = fixture.Verifier(supervisor);
        using var cancellation = new CancellationTokenSource();
        var certified = new MediaWorkerStoppedCancellationException(cancellation.Token);
        supervisor.BeforeReply = _ =>
        {
            cancellation.Cancel();
            throw certified;
        };
        var store = fixture.Store(supervisor, [Reference()]);

        OperationCanceledException? error = null;
        try
        { await PrepareAsync(fixture.Service(verifier, store), source, "disc.chd", admitted, cancellation.Token); }
        catch (OperationCanceledException exception) { error = exception; }

        error.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        error.ShouldBeSameAs(certified);
        supervisor.Calls.ShouldBe(1);
        store.Calls.ShouldBe(0);
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeTrue();
        source.DisposeCalls.ShouldBe(0);
        source.QuarantineCalls.ShouldBe(0);
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_UnconfirmedChildStop_RetainsCustodyWithoutReferenceLookup(bool admitted)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        supervisor.BeforeReply = _ =>
        {
            source.TryBeginChildUse().ShouldBeTrue();
            supervisor.Stopped = false;
            return Task.CompletedTask;
        };
        using var verifier = fixture.Verifier(supervisor);
        var store = fixture.Store(supervisor, [Reference()]);

        var result = await PrepareAsync(fixture.Service(verifier, store), source, "disc.chd", admitted);

        result.FirstError.Code.ShouldBe("Rom.Media.Containment");
        store.Calls.ShouldBe(0);
        source.ChildBeginSucceeded.ShouldBeTrue("the fixture must establish active source custody before the containment boundary");
        source.QuarantineCalls.ShouldBeGreaterThan(0);
        source.TryEndChildUse().ShouldBeFalse();
        supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeFalse();
        File.Exists(supervisor.SeenLease!.RecordPath).ShouldBeTrue();
        await source.DisposeAsync();
        File.Exists(source.Path).ShouldBeTrue();
        File.Exists(source.Path + ".quarantine").ShouldBeTrue();
        await Should.ThrowAsync<ResourceCapacityException>(async () =>
        {
            using var unexpected = await fixture.Runtime.AcquireAsync();
        });
    }

    [Fact]
    public async Task Admission_BorrowedOuterLease_ResolvesAfterSupervisedReturnWithoutReleasingCallerReservation()
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        using var verifier = fixture.Verifier(supervisor);
        using var outer = await fixture.Runtime.AcquireAsync(Guid.NewGuid(), "ingestion");
        var store = new ReferenceStore([Reference()])
        {
            DuringFind = () =>
            {
                supervisor.Completed.ShouldBeTrue();
                supervisor.SeenLease.ShouldBeSameAs(outer);
                outer.Released.ShouldBeFalse();
                MediaProcessingRuntime.Current.ShouldBeSameAs(outer);
                return Task.CompletedTask;
            }
        };
        var service = fixture.Service(verifier, store);
        using (outer.Activate())
        {
            using var admission = (await service.AdmitAsync(Header(), "disc.chd")).ShouldNotBeNull();
            var result = await admission.PrepareAsync(source, "disc.chd");
            result.IsError.ShouldBeFalse();
            await using var prepared = result.Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();
            AssertImportedFacts(prepared, source);
            prepared.Eligibility.ShouldNotBeNull();
            admission.Dispose();
            outer.Released.ShouldBeFalse();
            store.Calls.ShouldBe(1);
        }
        outer.Dispose();
        outer.Released.ShouldBeTrue();
        source.DisposeCalls.ShouldBe(0);
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Fact]
    public async Task Admission_OnceConsumed_DoesNotInspectOrResolveReferencesTwice()
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        using var verifier = fixture.Verifier(supervisor);
        var store = fixture.Store(supervisor, [Reference()]);
        var service = fixture.Service(verifier, store);
        using var admission = (await service.AdmitAsync(Header(), "disc.chd")).ShouldNotBeNull();

        var result = await admission.PrepareAsync(source, "disc.chd");
        result.IsError.ShouldBeFalse();
        await using var prepared = result.Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();
        await Should.ThrowAsync<InvalidOperationException>(() => admission.PrepareAsync(source, "disc.chd"));

        supervisor.Calls.ShouldBe(1);
        store.Calls.ShouldBe(1);
        source.OpenReads.ShouldBe(0);
        source.DisposeCalls.ShouldBe(0);
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admission_CancelOrDisposeBeforeInspection_ReleasesReservationWithoutChildOrReference(bool cancel)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var supervisor = fixture.Supervisor(source);
        using var verifier = fixture.Verifier(supervisor);
        var store = fixture.Store(supervisor, [Reference()]);
        var service = fixture.Service(verifier, store);
        using var admission = (await service.AdmitAsync(Header(), "disc.chd")).ShouldNotBeNull();
        using var cancellation = new CancellationTokenSource();

        if (cancel)
        {
            cancellation.Cancel();
            OperationCanceledException? error = null;
            try
            { await admission.PrepareAsync(source, "disc.chd", cancellation.Token); }
            catch (OperationCanceledException exception) { error = exception; }
            error.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        }
        else
        {
            admission.Dispose();
            await Should.ThrowAsync<InvalidOperationException>(() => admission.PrepareAsync(source, "disc.chd"));
        }

        supervisor.Calls.ShouldBe(0);
        store.Calls.ShouldBe(0);
        source.DisposeCalls.ShouldBe(0);
        source.QuarantineCalls.ShouldBe(0);
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Fact]
    public async Task AdmitAsync_AlreadyCanceled_DoesNotResolveFactoryOrReserveCapacity()
    {
        int factoryCalls = 0;
        var service = new RomPreparationService(new RomPreparationAdapterRegistry(new HashingService(),
            () => throw new InvalidOperationException("No RVZ route."),
            () => throw new InvalidOperationException("No original route."),
            () => { factoryCalls++; throw new InvalidOperationException("Cancellation must precede factory resolution."); },
            () => throw new NotSupportedException("This fixture has no raw CD inspection.")), new Romd.Admin.Application.Source.Rom.RomPs2DvdEligibilityResolver(new MediaInfrastructureTestServices.EmptyReferences()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException? error = null;
        try
        { await service.AdmitAsync(Header(), "disc.chd", cancellation.Token); }
        catch (OperationCanceledException exception) { error = exception; }

        error.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        factoryCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("disc.chd", false)]
    [InlineData("disc.iso", false)]
    [InlineData("disc.bin", false)]
    [InlineData("disc.rvz", false)]
    [InlineData("disc.chd", true)]
    [InlineData("disc.iso", true)]
    [InlineData("disc.bin", true)]
    [InlineData("disc.rvz", true)]
    public async Task PrepareAsync_ChdWithoutConfiguredFactory_FailsClosedBeforeIdentityHashing(string filename, bool admitted)
    {
        using var fixture = new Fixture();
        await using var source = await fixture.SourceAsync();
        var service = new RomPreparationService(new RomPreparationAdapterRegistry(fixture.Hashing,
            () => throw new InvalidOperationException("No RVZ fallback."),
            () => throw new InvalidOperationException("No original fallback."),
            () => throw new NotSupportedException("CHD preparation requires configured native assets."),
            () => throw new NotSupportedException("This fixture has no raw CD inspection.")), new Romd.Admin.Application.Source.Rom.RomPs2DvdEligibilityResolver(new MediaInfrastructureTestServices.EmptyReferences()));
        service.RequiresDatHashes(Magic, filename).ShouldBeFalse();

        var error = await Should.ThrowAsync<NotSupportedException>(() => PrepareAsync(service, source, filename, admitted));

        error.Message.ShouldBe("CHD preparation requires configured native assets.");
        fixture.Hashing.ReceivedCalls().ShouldBeEmpty();
        source.DisposeCalls.ShouldBe(0);
        source.QuarantineCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("MCom")]
    [InlineData("MComprH")]
    [InlineData("not-chd!")]
    public async Task AdmitAsync_ChdExtensionWithoutCompleteMagic_RejectsBeforeAnyFactory(string header)
    {
        var service = new RomPreparationService(new RomPreparationAdapterRegistry(new HashingService(),
            () => throw new InvalidOperationException("No RVZ fallback."),
            () => throw new InvalidOperationException("No original fallback."),
            () => throw new InvalidOperationException("Malformed admission must precede native factory resolution."),
            () => throw new NotSupportedException("This fixture has no raw CD inspection.")), new Romd.Admin.Application.Source.Rom.RomPs2DvdEligibilityResolver(new MediaInfrastructureTestServices.EmptyReferences()));
        service.RequiresDatHashes(System.Text.Encoding.ASCII.GetBytes(header), "disc.chd").ShouldBeFalse();

        (await Should.ThrowAsync<InvalidDataException>(() => service.AdmitAsync(System.Text.Encoding.ASCII.GetBytes(header), "disc.chd"))).Message.ShouldBe("CHD preparation requires a complete standalone v5 CD or DVD header.");
    }

    [Theory]
    [InlineData("M", "disc.bin")]
    [InlineData("MCom", "disc.bin")]
    [InlineData("MComprH", "disc.bin")]
    [InlineData("MComxxxx", "disc.bin")]
    public async Task PrepareAsync_PartialOrFalseMagicWithoutChdHint_PreservesOrdinaryIdentity(string header, string filename)
    {
        using var fixture = new Fixture();
        await using var source = new MemoryInput(System.Text.Encoding.ASCII.GetBytes(header));
        var service = new RomPreparationService(new RomPreparationAdapterRegistry(new HashingService(),
            () => throw new InvalidOperationException("No RVZ route."),
            () => throw new InvalidOperationException("No original route."),
            () => throw new InvalidOperationException("Incomplete magic is not a CHD claim."),
            () => throw new NotSupportedException("This fixture has no raw CD inspection.")), new Romd.Admin.Application.Source.Rom.RomPs2DvdEligibilityResolver(new MediaInfrastructureTestServices.EmptyReferences()));

        var result = await service.PrepareAsync(source, filename);

        result.IsError.ShouldBeFalse();
        await using var prepared = result.Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();
        prepared.Encoding.ShouldBe(RomRepresentationEncoding.Identity);
        prepared.ContentSha256.ShouldBe(source.FileSha256);
        prepared.Hashes.BytesRead.ShouldBe(header.Length);
        prepared.LogicalFilename.ShouldBe(filename);
        prepared.ChdIntegrity.ShouldBeNull();
        prepared.ChdPs2Boot.ShouldBeNull();
        prepared.Ps2Observation.ShouldBeNull();
        prepared.Eligibility.ShouldBeNull();
        source.ReadBytes.ShouldBe(header.Length + Math.Min(8, header.Length));
        source.DisposeCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("chd", "disc.iso", 124, RomRepresentationEncoding.Chd)]
    [InlineData("chd", "disc.bin", 124, RomRepresentationEncoding.Chd)]
    [InlineData("rvz", "disc.chd", 4, RomRepresentationEncoding.Rvz)]
    [InlineData("nintendo", "disc.iso", 32, RomRepresentationEncoding.Identity)]
    [InlineData("nintendo", "disc.gcm", 32, RomRepresentationEncoding.Identity)]
    [InlineData("iso", "disc.iso", RomPreparationDetection.MaximumPrefixBytes, RomRepresentationEncoding.Identity)]
    [InlineData("false-chd-iso", "disc.iso", RomPreparationDetection.MaximumPrefixBytes, RomRepresentationEncoding.Identity)]
    [InlineData("false-chd-nintendo", "disc.iso", RomPreparationDetection.MaximumPrefixBytes, RomRepresentationEncoding.Identity)]
    [InlineData("false-chd-nintendo", "disc.gcm", 124, RomRepresentationEncoding.Identity)]
    public void SelectPrefixLength_TwoRoundCaller_ReachesExactRouteAndBound(string kind, string filename, int expectedBytes, RomRepresentationEncoding encoding)
    {
        var bytes = Prefix(kind);
        int selected = 4;
        for (int round = 0; round < 2; round++)
        {
            int next = RomPreparationDetection.SelectPrefixLength(bytes.AsMemory(0, selected), filename);
            if (next <= selected)
                break;
            next.ShouldBeInRange(4, RomPreparationDetection.MaximumPrefixBytes);
            selected = next;
        }
        var registry = new RomPreparationAdapterRegistry(new HashingService(),
            () => throw new InvalidOperationException("Detection does not launch RVZ."),
            () => throw new InvalidOperationException("Detection does not launch original inspection."),
            () => throw new InvalidOperationException("Detection does not launch CHD."),
            () => throw new NotSupportedException("This fixture has no raw CD inspection."));

        selected.ShouldBe(expectedBytes);
        registry.Detect(bytes.AsSpan(0, selected), filename).Encoding.ShouldBe(encoding);
        registry.Detect(bytes.AsSpan(0, selected), filename).RequiresDatHashes(bytes.AsSpan(0, selected)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("disc.chd")]
    [InlineData("disc.iso")]
    [InlineData("disc.bin")]
    [InlineData("disc.rvz")]
    public async Task CreateAsync_ExistingStagingCallbacks_ExtendsChdPrefixAndAcquiresAdmissionBeforeCopy(string filename)
    {
        using var fixture = new Fixture();
        var supervisor = fixture.Supervisor(new MemoryInput(Header()));
        using var verifier = fixture.Verifier(supervisor);
        var store = fixture.Store(supervisor, []);
        var service = fixture.Service(verifier, store);
        IRomPreparationAdmission? admission = null;
        byte[]? seenPrefix = null;
        await using var input = new MemoryStream(Header());
        await using var staged = await fixture.Files.CreateAsync(input,
            includeDatHashes: prefix => service.RequiresDatHashes(prefix, filename),
            beforeCopy: async (prefix, ct) =>
            {
                seenPrefix = prefix.ToArray();
                admission = await service.AdmitAsync(prefix, filename, ct);
                admission.ShouldNotBeNull();
                await Should.ThrowAsync<ResourceCapacityException>(async () =>
                {
                    using var unexpected = await fixture.Runtime.AcquireAsync();
                });
            },
            selectPrefixLength: prefix => RomPreparationDetection.SelectPrefixLength(prefix, filename));
        using var ownedAdmission = admission.ShouldNotBeNull();

        seenPrefix.ShouldBe(MediaInfrastructureTestServices.ChdV5Prefix(2048));
        staged.DatHashes.ShouldBeNull();
        staged.FileSize.ShouldBe(Header().Length);
        using var copied = staged.OpenRead();
        using var actual = new MemoryStream();
        await copied.CopyToAsync(actual);
        actual.ToArray().ShouldBe(Header());
        admission!.Dispose();
        using var recovered = await fixture.Runtime.AcquireAsync();
    }

    [Theory]
    [InlineData("iso", "disc.iso", RomPreparationDetection.MaximumPrefixBytes)]
    [InlineData("false-chd-iso", "disc.iso", RomPreparationDetection.MaximumPrefixBytes)]
    [InlineData("nintendo", "disc.gcm", 32)]
    [InlineData("false-chd-nintendo", "disc.gcm", 124)]
    public async Task PrepareAsync_OriginalPrefixRegressions_ReachOriginalIdentifierWithoutChdOrIdentityFallback(string kind, string filename, int expectedRead)
    {
        byte[] bytes = Prefix(kind);
        await using var source = new MemoryInput(bytes);
        var proof = new RomVerification(source.FileSha256, source.FileSize, RomRepresentationEncoding.Identity,
            source.FileSha256, bytes.Length, Sha1.FromSpan(System.Security.Cryptography.SHA1.HashData(bytes)),
            Md5.FromSpan(System.Security.Cryptography.MD5.HashData(bytes)), Crc32.FromUInt32(System.IO.Hashing.Crc32.HashToUInt32(bytes)),
            "original-fixture/v1", "decoder/test", "worker/test", DateTimeOffset.UnixEpoch);
        var original = Substitute.For<IRomOriginalMediaIdentifier>();
        original.IdentifyAsync(source, Arg.Any<CancellationToken>()).Returns(new RomOriginalMediaIdentification(proof, new(Guid.NewGuid(), proof.Identity, "raw-original-fixture/v1", proof.DecoderBuild, proof.VerifiedAt, bytes.AsSpan(0, 32)), null));
        var hashing = Substitute.For<IHashingService>();
        hashing.ComputeDatHashesAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns<DatHashResult>(_ => throw new InvalidOperationException("An original prefix must not reach Identity hashing."));
        var service = new RomPreparationService(new RomPreparationAdapterRegistry(hashing,
            () => throw new InvalidOperationException("No RVZ fallback."),
            () => original,
            () => throw new InvalidOperationException("An original header is not CHD."),
            () => throw new NotSupportedException("This fixture has no raw CD inspection.")), new Romd.Admin.Application.Source.Rom.RomPs2DvdEligibilityResolver(new MediaInfrastructureTestServices.EmptyReferences()));

        var result = await service.PrepareAsync(source, filename);

        result.IsError.ShouldBeFalse();
        await using var prepared = result.Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();
        prepared.Verification.ShouldBeSameAs(proof);
        prepared.Encoding.ShouldBe(RomRepresentationEncoding.Identity);
        prepared.LogicalFilename.ShouldBe(filename);
        prepared.ChdIntegrity.ShouldBeNull();
        prepared.ChdPs2Boot.ShouldBeNull();
        prepared.Eligibility.ShouldBeNull();
        source.ReadBytes.ShouldBe(expectedRead);
        await original.Received(1).IdentifyAsync(source, Arg.Any<CancellationToken>());
        hashing.ReceivedCalls().ShouldBeEmpty();
        source.DisposeCalls.ShouldBe(0);
    }

    [Fact]
    public async Task CreateAsync_FalseChdIsoPrefix_PreservesDescriptorRouteWithinTwoRounds()
    {
        using var fixture = new Fixture();
        var original = Substitute.For<IRomOriginalMediaIdentifier>();
        var inner = Substitute.For<IRomOriginalMediaIdentificationAdmission>();
        original.AcquireAsync(Arg.Any<CancellationToken>()).Returns(inner);
        var service = new RomPreparationService(new RomPreparationAdapterRegistry(fixture.Hashing,
            () => throw new InvalidOperationException("No RVZ route."),
            () => original,
            () => throw new InvalidOperationException("False CHD prefix must reach original ISO route."),
            () => throw new NotSupportedException("This fixture has no raw CD inspection.")), new Romd.Admin.Application.Source.Rom.RomPs2DvdEligibilityResolver(new MediaInfrastructureTestServices.EmptyReferences()));
        IRomPreparationAdmission? admission = null;
        byte[]? seenPrefix = null;
        await using var input = new MemoryStream(Prefix("false-chd-iso"));
        await using var staged = await fixture.Files.CreateAsync(input,
            includeDatHashes: prefix => service.RequiresDatHashes(prefix, "disc.iso"),
            beforeCopy: async (prefix, ct) => { seenPrefix = prefix.ToArray(); admission = await service.AdmitAsync(prefix, "disc.iso", ct); },
            selectPrefixLength: prefix => RomPreparationDetection.SelectPrefixLength(prefix, "disc.iso"));
        using var ownedAdmission = admission.ShouldNotBeNull();

        seenPrefix.ShouldNotBeNull().Length.ShouldBe(RomPreparationDetection.MaximumPrefixBytes);
        staged.DatHashes.ShouldBeNull();
        await original.Received(1).AcquireAsync(Arg.Any<CancellationToken>());
        admission!.Dispose();
        inner.Received(1).Dispose();
    }

    private static async Task<ErrorOr<PreparedRom>> PrepareAsync(RomPreparationService service, ITempFile source,
        string filename, bool admitted, CancellationToken ct = default)
    {
        if (!admitted)
            return await service.PrepareAsync(source, filename, ct);
        using var admission = (await service.AdmitAsync(Header(), filename, ct)).ShouldNotBeNull();
        return await admission.PrepareAsync(source, filename, ct);
    }

    private static void AssertImportedFacts(PreparedRom.WholeFile prepared, ITempFile source)
    {
        prepared.Artifact.ShouldBeSameAs(source);
        prepared.Encoding.ShouldBe(RomRepresentationEncoding.Chd);
        prepared.Verification.Profile.ShouldBe(ChdVerifier.Profile);
        prepared.ContentSha256.ShouldBe(Sha256.Parse(new string('a', 64)));
        prepared.Hashes.ShouldBe(new DatHashResult(Sha1.Parse(new string('b', 40)), Md5.Parse(new string('c', 32)), Crc32.Parse("12345678"), 131072));
        var integrity = prepared.ChdIntegrity.ShouldNotBeNull();
        integrity.MatchesMeasurement(prepared.Verification).ShouldBeTrue();
        integrity.Parameters.CapturedEncodingProfile.ShouldBe(prepared.EncodingProfile);
        prepared.Verification.CapturedEncodingProfile.ShouldBe(prepared.EncodingProfile);
        prepared.EncodingProfile.ShouldStartWith("chd-observed/v1;");
        prepared.ChdPs2Boot.ShouldNotBeNull().RootDirectoryBytes.ShouldBe(864U);
        prepared.Ps2Observation.ShouldBeNull();
        prepared.SourceEquivalence.ShouldBeNull();
        prepared.ChdEncodingEvidence.ShouldBeNull();
    }

    private static byte[] Header()
    {
        byte[] bytes = new byte[128];
        MediaInfrastructureTestServices.ChdV5Prefix(2048).CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] Prefix(string kind)
    {
        byte[] bytes = new byte[RomPreparationDetection.MaximumPrefixBytes];
        if (kind == "chd")
            Magic.CopyTo(bytes, 0);
        if (kind == "rvz")
            "RVZ\u0001"u8.CopyTo(bytes);
        if (kind.StartsWith("false-chd", StringComparison.Ordinal))
            "MComxxxx"u8.CopyTo(bytes);
        if (kind.Contains("nintendo", StringComparison.Ordinal))
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(28), 0xC2339F3D);
        if (kind is "iso" or "false-chd-iso")
        {
            bytes[16 * 2048] = 1;
            "CD001"u8.CopyTo(bytes.AsSpan(16 * 2048 + 1));
            bytes[16 * 2048 + 6] = 1;
        }
        return bytes;
    }

    private static MediaPs2Inspection Boot() => new(Ps2DvdIdentificationStatus.Identified, RomPs2BootLayoutFacts.ProfileVersion,
        new(131072, 64, 16, 24, 864, 25, 60, 26, 512,
            "cdrom0:\\SLUS_203.12;1", "cdrom0:\\SLUS_203.12;1", "SLUS_203.12", 0x100000, 1));

    private static RomDiscMediaReferenceRecord Reference(RomDiscReferenceSystem system = RomDiscReferenceSystem.PlayStation2,
        RomDiscReferenceMedia media = RomDiscReferenceMedia.Dvd5, int fileCount = 1, string? sha1 = null) =>
        new(Guid.NewGuid(), system, media, fileCount, 131072, Sha1.Parse(sha1 ?? new string('b', 40)), Md5.Parse(new string('c', 32)),
            Crc32.Parse("12345678"), 4096, Sha256.Parse(new string('e', 64)), "synthetic-trusted-record", "reference-parser/v1",
            "synthetic-local-import/v1", DateTimeOffset.UnixEpoch);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("romd-chd-preparation-").FullName;
        public MediaProcessingOptions Options { get; } = new() { MaxConcurrent = 1, AdmissionTimeoutSeconds = 1 };
        public IHashingService Hashing { get; } = Substitute.For<IHashingService>();
        public MediaProcessingRuntime Runtime { get; }
        public LocalTempFileFactory Files { get; }
        public ChdNativeCandidate Native { get; }
        public Fixture()
        {
            var romd = Substitute.For<IRomdOptions>();
            romd.DataDirectory.Returns(_root);
            romd.MaxUploadBytes.Returns(8L << 20);
            Runtime = new(romd, Options);
            Files = new(romd, new FileSystem(), new TempStorageOptions
            { MaximumBytes = 16L << 20, MaximumFiles = 2, MinimumFreeBytes = 0, AdmissionTimeoutSeconds = 1 });
            Native = new(Path.Combine(_root, "absent-libchdr"),
                new("linux-x64", "libmoonlark_chdr.so", 100, new string('1', 64), new string('4', 40), "test-upstream", 1,
                    "test-family", new string('5', 64), true, true, true));
            Hashing.ComputeDatHashesAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns<DatHashResult>(_ => throw new InvalidOperationException("CHD must not be hashed through Identity."));
        }
        public async Task<CountingInput> SourceAsync() => new(await Files.CreateAsync(new MemoryStream(Header())));
        public ChdVerifier Verifier(IMediaWorkerSupervisor supervisor) => new(Options, supervisor, Runtime, Native);
        public RomPreparationService Service(ChdVerifier verifier, IRomDiscMediaReferenceStore store) =>
            new(new RomPreparationAdapterRegistry(Hashing,
            () => throw new InvalidOperationException("No RVZ fallback."),
            () => throw new InvalidOperationException("No original fallback."),
            () => verifier,
            () => throw new NotSupportedException("This fixture has no raw CD inspection.")),
                new RomPs2DvdEligibilityResolver(store));
        public ReferenceStore Store(InstrumentedSupervisor supervisor, IReadOnlyList<RomDiscMediaReferenceRecord> rows) =>
            new(rows)
            {
                DuringFind = async () =>
                {
                    supervisor.Completed.ShouldBeTrue("reference resolution must follow the supervised operation");
                    supervisor.SeenLease.ShouldNotBeNull().Released.ShouldBeTrue("reference lookup must release owned inspection capacity");
                    MediaProcessingRuntime.Current.ShouldBeNull();
                    using var recovered = await Runtime.AcquireAsync();
                }
            };
        public InstrumentedSupervisor Supervisor(ITempFile source, bool boot = true)
        {
            var identity = new MediaWorkerIdentity(123, 123, Options.MemoryBytes, MediaInspectionService.DecoderBuild,
                "romd-media-worker/mvid:7300d4b9-e9d1-49d0-b6d3-17a09b6dc2b1", OperatingSystem.IsLinux()
                    ? new(Options.LinuxAddressSpaceBytes, Options.LinuxDataBytes, Options.CpuTimeoutSeconds, 0) : null,
                Options.MemoryBytes, 0);
            var measurement = new MediaInspection(source.FileSha256.ToString(), source.FileSize, new string('a', 64),
                new string('b', 40), new string('c', 32), "12345678", 131072, new string('0', 64), "chd-import/v1");
            var inspection = new ChdInspection(5, new(source.FileSize, 131072, 4096, 2048, 32, 0x7a6c6962, 0x6c7a6d61, 0, 0),
                new string('b', 40), new string('b', 40), new string('d', 40), new string('d', 40),
                new(RomChdDvdMetadataFacts.ProfileVersion, 1, 0x44564420, 1, 1, 0, RomChdDvdMetadataFacts.SingletonFingerprint.ToString()));
            return new(new(MediaWorkerStatus.Inspected, identity, measurement, NativeIdentity: Native.ExpectedIdentity,
                ChdInspection: inspection, Ps2Inspection: boot ? Boot() : null));
        }
        public void Dispose() => Directory.Delete(_root, true);
    }

    private sealed class ReferenceStore(IReadOnlyList<RomDiscMediaReferenceRecord> rows) : IRomDiscMediaReferenceStore
    {
        public ErrorOr<IReadOnlyList<RomDiscMediaReferenceRecord>> Response { get; set; } = ErrorOrFactory.From(rows);
        public Action? OnFind { get; set; }
        public Func<Task>? DuringFind { get; init; }
        public int Calls { get; private set; }
        public int LastLimit { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public RomDiscMediaReferenceQuery? LastQuery { get; private set; }
        public void SetRows(IReadOnlyList<RomDiscMediaReferenceRecord> records) => Response = ErrorOrFactory.From(records);
        public async Task<ErrorOr<IReadOnlyList<RomDiscMediaReferenceRecord>>> FindAsync(RomDiscMediaReferenceQuery query,
            int maximumResults, CancellationToken ct = default)
        {
            Calls++;
            LastQuery = query;
            LastLimit = maximumResults;
            LastToken = ct;
            if (DuringFind is { } during)
                await during();
            OnFind?.Invoke();
            return Response;
        }
    }

    private sealed class InstrumentedSupervisor(MediaWorkerReply reply) : IMediaWorkerSupervisor
    {
        public MediaWorkerFailure? Failure { get; set; }
        public bool Stopped { get; set; } = true;
        public bool Completed { get; private set; }
        public Func<CancellationToken, Task>? BeforeReply { get; set; }
        public int Calls { get; private set; }
        public MediaProcessingRuntime.MediaProcessingReservation? SeenLease { get; private set; }
        public bool IsAvailable => true;
        public async Task<MediaWorkerResult> InspectChdAsync(ITempFile source, CancellationToken ct = default)
        {
            Calls++;
            SeenLease = MediaProcessingRuntime.Current;
            if (BeforeReply is { } before)
                await before(ct);
            Completed = true;
            return new(reply, Failure, new(123, 0, 1024, 0, MediaWorkerDiagnosticKind.None), Stopped);
        }
        public Task<MediaWorkerResult> HandshakeAsync(CancellationToken ct = default) => throw new InvalidOperationException("No handshake fallback.");
        public Task<MediaWorkerResult> InspectAsync(ITempFile source, CancellationToken ct = default) => throw new InvalidOperationException("No RVZ fallback.");
        public Task<MediaWorkerResult> InspectIsoAsync(ITempFile source, CancellationToken ct = default) => throw new InvalidOperationException("No ISO fallback.");
        public Task<MediaWorkerResult> EncodeAsync(ITempFile source, IWritableTempFile output, long logicalBytes, RomIsoDiscKind discKind, CancellationToken ct = default) => throw new InvalidOperationException("No encoder fallback.");
    }

    private sealed class CountingInput(ITempFile inner) : ITempFile
    {
        public string Path => inner.Path;
        public Sha256 FileSha256 => inner.FileSha256;
        public long FileSize => inner.FileSize;
        public int OpenReads { get; private set; }
        public long ReadBytes { get; private set; }
        public int DisposeCalls { get; private set; }
        public int QuarantineCalls { get; private set; }
        public bool ChildBeginSucceeded { get; private set; }
        public Stream OpenRead() { OpenReads++; return new BoundedHeaderReadStream(inner.OpenRead(), 124, count => ReadBytes += count); }
        public bool TryBeginChildUse() => ChildBeginSucceeded = inner.TryBeginChildUse();
        public bool TryEndChildUse() => inner.TryEndChildUse();
        public bool Quarantine() { QuarantineCalls++; return inner.Quarantine(); }
        public ValueTask DisposeAsync() { DisposeCalls++; return inner.DisposeAsync(); }
    }

    private sealed class MemoryInput(byte[] bytes) : ITempFile
    {
        public string Path => "unused-prefix-memory-fixture";
        public Sha256 FileSha256 { get; } = Sha256.FromSpan(System.Security.Cryptography.SHA256.HashData(bytes));
        public long FileSize => bytes.Length;
        public long ReadBytes { get; private set; }
        public int DisposeCalls { get; private set; }
        public Stream OpenRead() => new CountingReadStream(new MemoryStream(bytes, false), count => ReadBytes += count);
        public bool TryBeginChildUse() => throw new InvalidOperationException("No child uses an ordinary prefix fixture.");
        public bool TryEndChildUse() => throw new InvalidOperationException("No child uses an ordinary prefix fixture.");
        public bool Quarantine() => throw new InvalidOperationException("No quarantine for ordinary prefix fixture.");
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }

    private sealed class CountingReadStream(Stream inner, Action<int> count) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int size) { int read = inner.Read(buffer, offset, size); count(read); return read; }
        public override int Read(Span<byte> buffer) { int read = inner.Read(buffer); count(read); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { int read = await inner.ReadAsync(buffer, ct); count(read); return read; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int size) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
    }
}
