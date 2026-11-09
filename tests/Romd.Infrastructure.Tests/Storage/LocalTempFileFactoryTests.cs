using System.IO.Abstractions;
using System.Security.Cryptography;
using NSubstitute;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Hashing;
using Romd.Infrastructure.Storage;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class LocalTempFileFactoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "romd-staging-test-" + Guid.NewGuid().ToString("N"));

    private LocalTempFileFactory Factory(long maxFile = 8, long total = 16, int slots = 2, int copyTimeout = 30)
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(_root);
        options.MaxUploadBytes.Returns(maxFile);
        return new(options, new FileSystem(), new TempStorageOptions
        {
            MaximumFiles = slots,
            MaximumBytes = total,
            MinimumFreeBytes = 0,
            AdmissionTimeoutSeconds = 1,
            CopyTimeoutSeconds = copyTimeout
        });
    }

    private static TempFileArtifactMeasurement Measured(byte[] bytes) => new(Sha256.FromSpan(SHA256.HashData(bytes)),
        Md5.FromSpan(MD5.HashData(bytes)), Sha1.FromSpan(SHA1.HashData(bytes)), bytes.Length);

    [Fact]
    public async Task ReserveCdAttemptAsync_AllSourcesAndGeneratedInputs_AreChargedBeforeCopyAndReclaimedTogether()
    {
        var factory = Factory(maxFile: 2 << 20, total: 8 << 20, slots: 5);
        Guid job = Guid.NewGuid();
        await using var attempt = await factory.ReserveCdAttemptAsync(job, [3, 4, 5], 1 << 20, CancellationToken.None);
        attempt.StagingBindings.Count.ShouldBe(6); // Five main slots plus the separate tool-CUE slot.
        attempt.StagingBindings.All(x => x.JobId == job && x.AttemptOperation == "encode-chd-cd").ShouldBeTrue();
        await Should.ThrowAsync<ResourceCapacityException>(() => factory.CreateAsync(new MemoryStream(new byte[] { 1 })));
        byte[] original = [1, 2, 3];
        await using var staged = await attempt.MaterializeAsync(0, new MemoryStream(original), Measured(original).Sha256, CancellationToken.None);
        staged.DatHashes!.BytesRead.ShouldBe(3);
        await Should.ThrowAsync<InvalidDataException>(() => attempt.MaterializeAsync(0, new MemoryStream(original), Measured(original).Sha256, CancellationToken.None));
        await using var cue = (await attempt.ReserveCdCueAsync(Romd.Media.Cd.ChdCdRequest.MaximumBytes)).Value;
        cue.Path.ShouldEndWith(".cue");
        await using var request = await attempt.CreateAsync(new MemoryStream("{}"u8.ToArray()));
        await Should.ThrowAsync<InvalidOperationException>(() => attempt.CreateAsync(new MemoryStream("{}"u8.ToArray())));
        await using var output = (await attempt.ReserveOutputAsync(1 << 20)).Value;
        await attempt.DisposeAsync();
        attempt.StagingBindings.All(x => !File.Exists(x.Path)).ShouldBeTrue();
        await using var next = await Factory(maxFile: 2 << 20, total: 8 << 20, slots: 5)
            .ReserveCdAttemptAsync(Guid.NewGuid(), [3, 4, 5], 1 << 20, CancellationToken.None);
    }

    [Fact]
    public async Task ReserveCdComparisonAsync_CompletePinnedInputs_HoldsOneAggregateWithoutAnEncodedOutput()
    {
        var factory = Factory(maxFile: 2 << 20, total: 2 << 20, slots: 4);
        Guid job = Guid.NewGuid();
        await using var attempt = await factory.ReserveCdComparisonAsync(job, 124, [3, 4], CancellationToken.None);
        attempt.StagingBindings.Count.ShouldBe(5); // Artifact, two originals and request; separate tool CUE.
        attempt.StagingBindings.All(binding => binding.JobId == job && binding.AttemptOperation == "verify-chd-cd").ShouldBeTrue();
        await Should.ThrowAsync<ResourceCapacityException>(() => factory.CreateAsync(new MemoryStream(new byte[] { 1 })));
        await Should.ThrowAsync<InvalidOperationException>(() => attempt.ReserveOutputAsync(124));
        byte[] bytes = [1, 2, 3];
        await using var original = await attempt.MaterializeAsync(1, new MemoryStream(bytes), Measured(bytes).Sha256, CancellationToken.None);
        await using var cue = (await attempt.ReserveCdCueAsync(Romd.Media.Cd.ChdCdRequest.MaximumBytes)).Value;
        await using var request = await attempt.CreateAsync(new MemoryStream("{}"u8.ToArray()));
        await attempt.DisposeAsync();
        attempt.StagingBindings.All(binding => !File.Exists(binding.Path)).ShouldBeTrue();
        await using var next = await factory.ReserveCdComparisonAsync(Guid.NewGuid(), 124, [3, 4], CancellationToken.None);
    }

    [Fact]
    public async Task ReserveCdComparisonAsync_MainAdmissionFails_ReleasesTheSeparateCueReservation()
    {
        var factory = Factory(maxFile: 2 << 20, total: 2 << 20, slots: 4);
        await using var occupied = await factory.CreateAsync(new MemoryStream(new byte[] { 1 }));
        await Should.ThrowAsync<ResourceCapacityException>(() => factory.ReserveCdComparisonAsync(Guid.NewGuid(), 124, [3, 4], CancellationToken.None));
        await occupied.DisposeAsync();
        await using var next = await factory.ReserveCdComparisonAsync(Guid.NewGuid(), 124, [3, 4], CancellationToken.None);
        next.StagingBindings.Count.ShouldBe(5);
    }

    [Fact]
    public async Task ReserveCdAttemptAsync_ConfiguredLimitsTooSmall_ReportStagingLimitsWithoutReserving()
    {
        // Three sources plus the generated request and output need five slots; a 200-byte ceiling refuses a 300-byte BIN.
        await Should.ThrowAsync<RomCdStagingLimitException>(() => Factory(maxFile: 2 << 20, total: 8 << 20, slots: 4)
            .ReserveCdAttemptAsync(Guid.NewGuid(), [3, 4, 5], 1 << 20, CancellationToken.None));
        await Should.ThrowAsync<RomCdStagingLimitException>(() => Factory(maxFile: 200, total: 8 << 20, slots: 5)
            .ReserveCdAttemptAsync(Guid.NewGuid(), [3, 4, 300], 124, CancellationToken.None));
        await using var next = await Factory(maxFile: 2 << 20, total: 8 << 20, slots: 5)
            .ReserveCdAttemptAsync(Guid.NewGuid(), [3, 4, 5], 1 << 20, CancellationToken.None);
    }

    [Fact]
    public async Task ReserveCdAttemptAsync_MainAdmissionFails_ReleasesItsToolCueAndPublishesNoPartialReservation()
    {
        var factory = Factory(maxFile: 2 << 20, total: 8 << 20, slots: 4);
        await using var occupied = await factory.CreateAsync(new MemoryStream(new byte[] { 1 }));
        await Should.ThrowAsync<ResourceCapacityException>(() => factory.ReserveCdAttemptAsync(Guid.NewGuid(), [3, 4], 1 << 20, CancellationToken.None));
        await occupied.DisposeAsync();
        await using var next = await factory.ReserveCdAttemptAsync(Guid.NewGuid(), [3, 4], 1 << 20, CancellationToken.None);
        next.StagingBindings.Count.ShouldBe(5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaterializeCdSourceAsync_ChangedLengthOrDigest_RejectsWithoutLosingTheAggregateCharge(bool extraByte)
    {
        var factory = Factory(maxFile: 2 << 20, total: 8 << 20, slots: 4);
        await using var attempt = await factory.ReserveCdAttemptAsync(Guid.NewGuid(), [3, 4], 1 << 20, CancellationToken.None);
        byte[] source = extraByte ? [1, 2, 3, 4] : [1, 2, 4];
        await Should.ThrowAsync<InvalidDataException>(() => attempt.MaterializeAsync(0, new MemoryStream(source), Measured([1, 2, 3]).Sha256, CancellationToken.None));
        await Should.ThrowAsync<ResourceCapacityException>(() => factory.CreateAsync(new MemoryStream(new byte[] { 1 })));
        await attempt.DisposeAsync();
        await using var next = await factory.ReserveCdAttemptAsync(Guid.NewGuid(), [3, 4], 1 << 20, CancellationToken.None);
    }

    [Fact]
    public async Task ReserveCdCueAsync_BoundedNamedPool_KeepsCustodyAndReclaimsOnlyAfterRelease()
    {
        var factory = Factory(maxFile: 1 << 20, total: 1 << 20, slots: 1);
        (await factory.ReserveCdCueAsync((512 << 10) + 1)).IsError.ShouldBeTrue();
        await using var output = (await factory.ReserveCdCueAsync(512 << 10)).Value;
        output.Path.ShouldEndWith(".cue");
        Path.GetFileName(Path.GetDirectoryName(output.Path)).ShouldBe("cd-cue-staging-v1");
        File.Exists(output.Path).ShouldBeFalse();
        await Should.ThrowAsync<ResourceCapacityException>(() => factory.ReserveCdCueAsync(1));
        // Descriptor reservations have a separate, small ledger; source/output staging is unchanged.
        await using var source = await factory.CreateAsync(new MemoryStream(new byte[] { 1 }));
        byte[] bytes = "REM generated descriptor\n"u8.ToArray();
        await File.WriteAllBytesAsync(output.Path, bytes);
        output.TryBeginChildUse().ShouldBeTrue();
        File.Exists(output.Path + ".quarantine").ShouldBeTrue();
        (await output.SealAsync(Measured(bytes))).IsError.ShouldBeTrue();
        output.TryEndChildUse().ShouldBeTrue();
        await using var sealedCue = (await output.SealAsync(Measured(bytes))).Value;
        await output.DisposeAsync();
        File.ReadAllBytes(sealedCue.Path).ShouldBe(bytes);
        await sealedCue.DisposeAsync();
        File.Exists(output.Path).ShouldBeFalse();
        await using var next = (await Factory(maxFile: 1 << 20, total: 1 << 20, slots: 1).ReserveCdCueAsync(1)).Value;
        next.Path.ShouldBe(output.Path);
    }

    [Fact]
    public async Task ReserveOutputAsync_RelativeConfiguredRoot_ReturnsFullyQualifiedBoundPath()
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(Path.GetRelativePath(Environment.CurrentDirectory, _root));
        options.MaxUploadBytes.Returns(8);
        var factory = new LocalTempFileFactory(options, new FileSystem(), new TempStorageOptions
        { MaximumFiles = 2, MaximumBytes = 16, MinimumFreeBytes = 0, AdmissionTimeoutSeconds = 1 });
        await using var output = (await factory.ReserveOutputAsync(8)).Value;
        Path.IsPathFullyQualified(output.Path).ShouldBeTrue();
        Path.GetDirectoryName(output.Path).ShouldBe(Path.Combine(_root, "temp", "staging-v1"));
    }

    [Fact]
    public async Task ReserveOutputAsync_EmptyPayload_ReservesWholeBoundBeforeChildAndSharesCopyBudget()
    {
        await using var output = (await Factory(total: 8, slots: 2).ReserveOutputAsync(8)).Value;
        File.Exists(output.Path).ShouldBeFalse();
        output.MaximumBytes.ShouldBe(8);
        File.ReadAllText(Path.Combine(_root, "temp", "staging-v1", "reservations.json")).ShouldContain("\"0\":8");
        await Should.ThrowAsync<ResourceCapacityException>(() => Factory(total: 8, slots: 2).CreateAsync(new MemoryStream([1])));
        output.TryBeginChildUse().ShouldBeTrue();
        File.Exists(output.Path + ".quarantine").ShouldBeTrue();
        output.TryEndChildUse().ShouldBeTrue();
    }

    [Fact]
    public async Task SealAsync_CompleteArtifact_TransfersSameLeaseShrinksBudgetAndKeepsLogicalHashesUnknown()
    {
        var factory = Factory(slots: 3);
        await using var output = (await factory.ReserveOutputAsync(8)).Value;
        await using var sibling = await factory.CreateAsync(new MemoryStream(new byte[8]));
        byte[] bytes = [1, 2, 3];
        await File.WriteAllBytesAsync(output.Path, bytes);
        output.TryBeginChildUse().ShouldBeTrue();
        (await output.SealAsync(Measured(bytes))).FirstError.Code.ShouldBe("Temp.OutputFacts");
        await Should.ThrowAsync<ResourceCapacityException>(() => factory.CreateAsync(new MemoryStream(new byte[5])));
        output.TryEndChildUse().ShouldBeTrue();
        var sealedResult = await output.SealAsync(Measured(bytes));
        sealedResult.IsError.ShouldBeFalse();
        await using var artifact = sealedResult.Value;
        artifact.Path.ShouldBe(output.Path);
        artifact.FileSha256.ShouldBe(Measured(bytes).Sha256);
        artifact.FileSize.ShouldBe(3);
        artifact.DatHashes.ShouldBeNull();
        await output.DisposeAsync();
        File.ReadAllBytes(artifact.Path).ShouldBe(bytes);
        (await output.SealAsync(Measured(bytes))).FirstError.Code.ShouldBe("Temp.OutputFacts");
        await using var recoveredCapacity = await factory.CreateAsync(new MemoryStream(new byte[5]));
        artifact.TryBeginChildUse().ShouldBeTrue();
        File.Exists(artifact.Path + ".quarantine").ShouldBeTrue();
        artifact.TryEndChildUse().ShouldBeTrue();
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("wrong-size")]
    [InlineData("oversize")]
    [InlineData("missing-output")]
    public async Task SealAsync_InvalidFacts_DoesNotTransferOrReduceReservation(string mutation)
    {
        await using var output = (await Factory().ReserveOutputAsync(8)).Value;
        byte[] bytes = [1, 2, 3];
        if (mutation != "missing-output")
            await File.WriteAllBytesAsync(output.Path, bytes);
        var facts = Measured(bytes);
        facts = mutation switch
        {
            "empty" => facts with { Sha256 = default },
            "wrong-size" => facts with { Size = 4 },
            "oversize" => facts with { Size = 9 },
            _ => facts
        };
        (await output.SealAsync(facts)).FirstError.Code.ShouldBe("Temp.OutputFacts");
        File.ReadAllText(Path.Combine(_root, "temp", "staging-v1", "reservations.json")).ShouldContain("\"0\":8");
    }

    [Fact]
    public async Task SealAsync_AccountingWriteFailure_KeepsConservativeReservationAndAllowsSafeRetry()
    {
        await using var output = (await Factory().ReserveOutputAsync(8)).Value;
        byte[] bytes = [1, 2, 3];
        await File.WriteAllBytesAsync(output.Path, bytes);
        string staging = Path.Combine(_root, "temp", "staging-v1", "reservations.new");
        Directory.CreateDirectory(staging);
        (await output.SealAsync(Measured(bytes))).FirstError.Code.ShouldBe("Temp.OutputIo");
        File.ReadAllText(Path.Combine(_root, "temp", "staging-v1", "reservations.json")).ShouldContain("\"0\":8");
        Directory.Delete(staging);
        await using var artifact = (await output.SealAsync(Measured(bytes))).Value;
        File.ReadAllText(Path.Combine(_root, "temp", "staging-v1", "reservations.json")).ShouldContain("\"0\":3");
    }

    [Fact]
    public async Task ReserveOutputAsync_ActiveHoldAtDisposal_RetainsPayloadAccountingAndRestartCustody()
    {
        var factory = Factory(total: 8, slots: 1);
        var output = (await factory.ReserveOutputAsync(8)).Value;
        output.TryBeginChildUse().ShouldBeTrue();
        await File.WriteAllBytesAsync(output.Path, new byte[] { 1, 2, 3 });
        await output.DisposeAsync();
        File.Exists(output.Path).ShouldBeTrue();
        File.Exists(output.Path + ".quarantine").ShouldBeTrue();
        await Should.ThrowAsync<ResourceCapacityException>(() => Factory(total: 8, slots: 1).ReserveOutputAsync(8));
        (await output.SealAsync(Measured([1, 2, 3]))).FirstError.Code.ShouldBe("Temp.OutputFacts");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inspection_UnconfirmedContainment_RetainsActualStagingAndAccounting(bool cancel)
    {
        using var child = new MediaWorkerTestFixture();
        var disc = new Romd.Media.TestSupport.SyntheticRvzBuilder(0x100000, 0x20000).Build();
        var source = await Factory(8L << 20, 8L << 20, 1).CreateAsync(new MemoryStream(disc.File));
        using var cancellation = new CancellationTokenSource();
        var supervisor = new MediaWorkerSupervisor(child.Options, () => new MediaWorkerProcessLifetime((_, _) =>
        {
            if (cancel)
                cancellation.Cancel();
            throw new MediaWorkerCleanupException("fixture stop observation failed");
        }));
        using var verifier = new RvzVerifier(child.Options, supervisor);
        try
        {
            if (cancel)
            {
                OperationCanceledException? canceled = null;
                try
                { await verifier.VerifyAsync(source, cancellation.Token); }
                catch (OperationCanceledException error) { canceled = error; }
                canceled.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
            }
            else
                (await verifier.VerifyAsync(source)).FirstError.Code.ShouldBe("Rom.Media.Containment");
        }
        finally { await source.DisposeAsync(); }
        File.Exists(source.Path).ShouldBeTrue("unconfirmed containment must retain its staged source");
        File.ReadAllBytes(source.Path).ShouldBe(disc.File);
        File.Exists(source.Path + ".quarantine").ShouldBeTrue();
        File.ReadAllText(Path.Combine(_root, "temp", "staging-v1", "reservations.json"))
            .ShouldContain($"\"0\":{disc.File.Length}");
        await Should.ThrowAsync<ResourceCapacityException>(() =>
            Factory(8L << 20, 8L << 20, 1).CreateAsync(new MemoryStream([1])));
    }

    [Fact]
    public async Task AcquireAsync_DurableChildUseHold_SurvivesOwnerExitAndIsNeverReclaimed()
    {
        var options = new TempStorageOptions { MaximumBytes = 8, MaximumFiles = 1, MinimumFreeBytes = 0, AdmissionTimeoutSeconds = 1 };
        var pool = new TempFilePool(new FileSystem(), _root, options, 8, () => 100);
        var lease = await pool.AcquireAsync(4, default);
        File.WriteAllBytes(lease.Path, [1, 2, 3, 4]);
        File.WriteAllBytes(lease.Path + ".quarantine", []);
        lease.Owner.Dispose(); // A host exit drops its OS lease, but not its durable child-use hold.
        var restarted = new TempFilePool(new FileSystem(), _root, options, 8, () => 100);
        await Should.ThrowAsync<ResourceCapacityException>(() => restarted.AcquireAsync(4, default));
        File.ReadAllBytes(lease.Path).ShouldBe(new byte[] { 1, 2, 3, 4 });
        File.ReadAllText(Path.Combine(_root, "reservations.json")).ShouldContain("\"0\":4");
    }

    [Fact]
    public async Task Inspection_ChildUseHold_PrecedesLaunchAndEndsAfterConfirmedCancellation()
    {
        using var child = new MediaWorkerTestFixture(inspectionMode: "gated");
        var disc = new Romd.Media.TestSupport.SyntheticRvzBuilder(0x100000, 0x20000).Build();
        var source = await Factory(8L << 20, 8L << 20, 1).CreateAsync(new MemoryStream(disc.File));
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = child.Supervisor().InspectAsync(source, cancellation.Token);
        int pid = await MediaWorkerTestFixture.WaitForPidAsync(source.Path + ".pid", deadline.Token);
        File.Exists(source.Path + ".quarantine").ShouldBeTrue();
        await cancellation.CancelAsync();
        OperationCanceledException? canceled = null;
        try
        { await pending; }
        catch (OperationCanceledException error) { canceled = error; }
        canceled.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        MediaWorkerTestFixture.IsRunning(pid).ShouldBeFalse();
        File.Exists(source.Path + ".quarantine").ShouldBeFalse();
        await source.DisposeAsync();
        File.Exists(source.Path).ShouldBeFalse();
        await using var replacement = await Factory(8L << 20, 8L << 20, 1).CreateAsync(new MemoryStream([1]));
    }

    [Fact]
    public async Task Inspection_ChildUseHoldCannotPersist_RejectsBeforeLaunchAndReleasesUnusedSource()
    {
        using var child = new MediaWorkerTestFixture();
        var source = await Factory(slots: 1).CreateAsync(new MemoryStream([82, 86, 90, 1]));
        Directory.CreateDirectory(source.Path + ".quarantine");
        using var verifier = child.Verifier();
        var result = await verifier.VerifyAsync(source);
        result.FirstError.Code.ShouldBe("Rom.Media.SourceCustody");
        File.Exists(source.Path + ".pid").ShouldBeFalse();
        using var available = await verifier.AcquireAsync(default);
        Directory.Delete(source.Path + ".quarantine");
        await source.DisposeAsync();
        File.Exists(source.Path).ShouldBeFalse();
        await using var replacement = await Factory(slots: 1).CreateAsync(new MemoryStream([1]));
    }

    [Fact]
    public async Task Quarantine_MarkerPersistenceFails_StillRetainsLiveLeaseAndAccounting()
    {
        var source = await Factory(slots: 1).CreateAsync(new MemoryStream([1, 2, 3, 4]));
        Directory.CreateDirectory(source.Path + ".quarantine");
        source.Quarantine().ShouldBeFalse();
        await source.DisposeAsync();
        File.ReadAllBytes(source.Path).ShouldBe(new byte[] { 1, 2, 3, 4 });
        await Should.ThrowAsync<ResourceCapacityException>(() => Factory(slots: 1).CreateAsync(new MemoryStream([1])));
    }

    [Fact]
    public async Task CreateAsync_Success_HashesRemainingBytesAndDisposalReclaimsCapacity()
    {
        var factory = Factory(slots: 1);
        using var input = new MemoryStream([1, 2, 3, 4]);
        input.Position = 1;
        var file = await factory.CreateAsync(input, includeDatHashes: _ => true);
        file.FileSize.ShouldBe(3);
        var hashes = file.DatHashes.ShouldNotBeNull();
        hashes.Sha1.ShouldBe(Sha1.FromSpan(SHA1.HashData(new byte[] { 2, 3, 4 })));
        hashes.Md5.ShouldBe(Md5.FromSpan(MD5.HashData(new byte[] { 2, 3, 4 })));
        hashes.Crc32.ShouldBe(Crc32.FromLittleEndian(System.IO.Hashing.Crc32.Hash(new byte[] { 2, 3, 4 })));
        file.FileSha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(new byte[] { 2, 3, 4 })));
        File.ReadAllBytes(file.Path).ShouldBe(new byte[] { 2, 3, 4 });
        await file.DisposeAsync();
        await file.DisposeAsync();
        File.Exists(file.Path).ShouldBeFalse();
        Should.Throw<ObjectDisposedException>(() => file.OpenRead());
        await using var replacement = await factory.CreateAsync(new MemoryStream([5]));
        replacement.FileSize.ShouldBe(1);
    }

    [Fact]
    public async Task CreateAsync_TwoFactoryInstances_RespectSharedBudgetAndProtectLiveFiles()
    {
        await using var first = await Factory(total: 8).CreateAsync(new MemoryStream(new byte[8]));
        (await Should.ThrowAsync<ResourceCapacityException>(() => Factory(total: 8).CreateAsync(new MemoryStream([1]))))
            .Message.ShouldContain("capacity");
        File.Exists(first.Path).ShouldBeTrue();
        File.ReadAllBytes(first.Path).Length.ShouldBe(8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAsync_DefaultOrUnneededDatHashes_OnlyHashesArtifact(bool detect)
    {
        using var source = new MemoryStream([82, 86, 90, 1, 2]);
        ReadOnlyMemory<byte> observed = default;
        await using var file = await Factory().CreateAsync(source, includeDatHashes: detect ? magic =>
        {
            observed = magic.ToArray();
            return false;
        }
        : null);
        file.DatHashes.ShouldBeNull();
        file.FileSha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(new byte[] { 82, 86, 90, 1, 2 })));
        File.ReadAllBytes(file.Path).ShouldBe(new byte[] { 82, 86, 90, 1, 2 });
        if (detect)
            observed.ToArray().ShouldBe(new byte[] { 82, 86, 90, 1 });
        source.CanRead.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(8)]
    public async Task CreateAsync_NonSeekableDetectionPrefix_HashesAndCopiesAllBytesOnce(int length)
    {
        using var source = new NonSeekableInput(Enumerable.Range(0, length).Select(i => (byte)i).ToArray());
        await using var file = await Factory().CreateAsync(source, includeDatHashes: magic =>
        {
            magic.Length.ShouldBe(Math.Min(length, 4));
            return true;
        });
        var bytes = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        file.FileSha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(bytes)));
        file.DatHashes.ShouldNotBeNull().Sha1.ShouldBe(Sha1.FromSpan(SHA1.HashData(bytes)));
        File.ReadAllBytes(file.Path).ShouldBe(bytes);
        source.Position.ShouldBe(length);
    }

    private sealed class NonSeekableInput(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(28)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(64)]
    public async Task CreateAsync_AdaptiveShortNonSeekablePrefix_ReplaysEveryByteAndCallsDecisionsOnce(int length)
    {
        byte[] bytes = Enumerable.Range(0, length).Select(index => (byte)index).ToArray();
        using var source = new GuardedPrefixInput(bytes) { MaximumReadBytes = 3, Admitted = true };
        int hashingCalls = 0, admissionCalls = 0;

        await using var staged = await Factory(128, 256).CreateAsync(source,
            includeDatHashes: prefix =>
            {
                hashingCalls++;
                prefix.ToArray().ShouldBe(bytes[..Math.Min(length, RomPreparationDetection.MaximumPrefixBytes)]);
                return true;
            }, beforeCopy: (prefix, _) =>
            {
                admissionCalls++;
                prefix.Length.ShouldBe(Math.Min(length, RomPreparationDetection.MaximumPrefixBytes));
                source.Position.ShouldBe(prefix.Length);
                return Task.CompletedTask;
            }, selectPrefixLength: prefix => RomPreparationDetection.SelectPrefixLength(prefix, "ordinary.iso"));

        File.ReadAllBytes(staged.Path).ShouldBe(bytes);
        staged.FileSha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(bytes)));
        staged.DatHashes!.Sha1.ShouldBe(Sha1.FromSpan(SHA1.HashData(bytes)));
        source.Position.ShouldBe(length);
        hashingCalls.ShouldBe(1);
        admissionCalls.ShouldBe(1);
    }

    [Fact]
    public async Task CreateAsync_RvzRenamedIso_AdmitsAfterFourBytesBeforeByteFive()
    {
        byte[] bytes = [82, 86, 90, 1, 2, 3, 4, 5];
        using var source = new GuardedPrefixInput(bytes);
        using var verifier = new RvzVerifier(new() { MaxConcurrent = 1, AdmissionTimeoutSeconds = 1 });
        var service = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => verifier, new MediaInfrastructureTestServices.EmptyReferences());
        IRomPreparationAdmission? admission = null;
        try
        {
            await using var staged = await Factory().CreateAsync(source,
                includeDatHashes: prefix => service.RequiresDatHashes(prefix, "renamed.iso"),
                beforeCopy: async (prefix, ct) =>
                {
                    prefix.Length.ShouldBe(4);
                    source.Position.ShouldBe(4);
                    admission = await service.AdmitAsync(prefix, "renamed.iso", ct);
                    admission.ShouldNotBeNull();
                    source.Admitted = true;
                }, selectPrefixLength: prefix => RomPreparationDetection.SelectPrefixLength(prefix, "renamed.iso"));

            File.ReadAllBytes(staged.Path).ShouldBe(bytes);
            staged.DatHashes.ShouldBeNull();
            await Should.ThrowAsync<ResourceCapacityException>(() => verifier.AcquireAsync(CancellationToken.None));
        }
        finally { admission?.Dispose(); }
    }

    [Fact]
    public async Task CreateAsync_InvalidRvzExtension_RejectsBeforeByteFive()
    {
        using var source = new GuardedPrefixInput([1, 2, 3, 4, 5, 6, 7, 8]);
        var service = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => throw new InvalidOperationException("An invalid RVZ prefix must fail before resolving the verifier."), new MediaInfrastructureTestServices.EmptyReferences());

        await Should.ThrowAsync<InvalidDataException>(() => Factory().CreateAsync(source,
            beforeCopy: async (prefix, ct) => { using var admission = await service.AdmitAsync(prefix, "invalid.rvz", ct); },
            selectPrefixLength: prefix => RomPreparationDetection.SelectPrefixLength(prefix, "invalid.rvz")));

        source.Position.ShouldBe(4);
        Payloads().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(RomPreparationDetection.MaximumPrefixBytes + 1)]
    [InlineData(int.MaxValue)]
    public async Task CreateAsync_InvalidPrefixSelection_RejectsBeforeAllocationOrCallbacks(int selected)
    {
        using var source = new GuardedPrefixInput([1, 2, 3, 4, 5, 6, 7, 8]);
        int calls = 0;

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Factory().CreateAsync(source,
            includeDatHashes: _ => { calls++; return true; },
            beforeCopy: (_, _) => { calls++; return Task.CompletedTask; },
            selectPrefixLength: _ => selected));

        source.Position.ShouldBe(4);
        calls.ShouldBe(0);
        Payloads().ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateAsync_UnknownIsoWithOccupiedMediaSlot_StagesIdentityWithoutResolvingVerifier()
    {
        byte[] bytes = Enumerable.Repeat((byte)0x5A, 64).ToArray();
        using var source = new GuardedPrefixInput(bytes) { Admitted = true };
        using var verifier = new RvzVerifier(new() { MaxConcurrent = 1, AdmissionTimeoutSeconds = 1 });
        using var occupied = await verifier.AcquireAsync(CancellationToken.None);
        var service = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => throw new InvalidOperationException("Unknown ISO staging must not resolve the occupied verifier."), new MediaInfrastructureTestServices.EmptyReferences());
        IRomPreparationAdmission? admission = null;

        await using var staged = await Factory(128, 256).CreateAsync(source,
            includeDatHashes: prefix => service.RequiresDatHashes(prefix, "ordinary.iso"),
            beforeCopy: async (prefix, ct) => admission = await service.AdmitAsync(prefix, "ordinary.iso", ct),
            selectPrefixLength: prefix => RomPreparationDetection.SelectPrefixLength(prefix, "ordinary.iso"));
        await using var prepared = (await service.PrepareAsync(staged, "ordinary.iso")).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();

        admission.ShouldBeNull();
        staged.DatHashes.ShouldNotBeNull();
        File.ReadAllBytes(staged.Path).ShouldBe(bytes);
        prepared.Verification.Profile.ShouldBe("identity/v1");
        prepared.Eligibility.ShouldBeNull();
    }

    [Fact]
    public async Task CreateAsync_RecognizedNintendoPrefix_ReservesMediaBeforeCopyingRemainingBytes()
    {
        using var source = new GuardedPrefixInput(SyntheticNintendoIso.GameCube()) { Admitted = true };
        using var verifier = new RvzVerifier(new() { MaxConcurrent = 1, AdmissionTimeoutSeconds = 1 });
        using var occupied = await verifier.AcquireAsync(CancellationToken.None);
        var service = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => verifier, new MediaInfrastructureTestServices.EmptyReferences());

        await Should.ThrowAsync<ResourceCapacityException>(() => Factory(8L << 20, 8L << 20).CreateAsync(source,
            includeDatHashes: prefix => service.RequiresDatHashes(prefix, "disc.iso"),
            beforeCopy: async (prefix, ct) => { using var admission = await service.AdmitAsync(prefix, "disc.iso", ct); },
            selectPrefixLength: prefix => RomPreparationDetection.SelectPrefixLength(prefix, "disc.iso")));

        source.Position.ShouldBe(32);
        Payloads().ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateAsync_Ps2DescriptorPrefix_AdmitsBeforePayloadAndReplaysOriginalBytesExactlyOnce()
    {
        byte[] bytes = SyntheticPs2Iso.Create();
        using var source = new GuardedPrefixInput(bytes) { Admitted = true, MaximumReadBytes = 7 };
        var identifier = Substitute.For<IRomOriginalMediaIdentifier>();
        var slot = Substitute.For<IRomOriginalMediaIdentificationAdmission>();
        identifier.AcquireAsync(Arg.Any<CancellationToken>()).Returns(slot);
        var service = new RomPreparationService(new RomPreparationAdapterRegistry(new HashingService(),
            () => throw new InvalidOperationException("Prefix routing cannot resolve a verifier."),
            () => identifier,
            () => throw new NotSupportedException("This fixture has no native DVD inspection."),
            () => throw new NotSupportedException("This fixture has no raw CD inspection.")), new Romd.Admin.Application.Source.Rom.RomPs2DvdEligibilityResolver(new MediaInfrastructureTestServices.EmptyReferences()));
        IRomPreparationAdmission? admission = null;
        var selectedLengths = new List<int>();
        try
        {
            await using var staged = await Factory(1L << 20, 2L << 20).CreateAsync(source,
                includeDatHashes: prefix => service.RequiresDatHashes(prefix, "disc.iso"),
                beforeCopy: async (prefix, ct) =>
                {
                    prefix.Length.ShouldBe(RomPreparationDetection.MaximumPrefixBytes);
                    source.Position.ShouldBe(RomPreparationDetection.MaximumPrefixBytes);
                    Payloads().ShouldBeEmpty();
                    admission = await service.AdmitAsync(prefix, "disc.iso", ct);
                },
                selectPrefixLength: prefix =>
                {
                    selectedLengths.Add(prefix.Length);
                    return RomPreparationDetection.SelectPrefixLength(prefix, "disc.iso");
                });

            selectedLengths.ShouldBe(new[] { 4, 32 });
            admission.ShouldNotBeNull();
            staged.DatHashes.ShouldBeNull();
            staged.FileSize.ShouldBe(bytes.Length);
            staged.FileSha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(bytes)));
            File.ReadAllBytes(staged.Path).ShouldBe(bytes);
            source.Position.ShouldBe(bytes.Length);
            await identifier.Received(1).AcquireAsync(Arg.Any<CancellationToken>());
        }
        finally { admission?.Dispose(); }
        slot.Received(1).Dispose();
    }

    [Fact]
    public async Task CreateAsync_Ps2DescriptorWithOccupiedMediaSlot_StopsAtBoundWithoutStagingPayload()
    {
        using var source = new GuardedPrefixInput(SyntheticPs2Iso.Create()) { Admitted = true };
        using var verifier = new RvzVerifier(new() { MaxConcurrent = 1, AdmissionTimeoutSeconds = 1 });
        using var occupied = await verifier.AcquireAsync(CancellationToken.None);
        var service = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => verifier, new MediaInfrastructureTestServices.EmptyReferences());

        await Should.ThrowAsync<ResourceCapacityException>(() => Factory(1L << 20, 2L << 20).CreateAsync(source,
            includeDatHashes: prefix => service.RequiresDatHashes(prefix, "disc.iso"),
            beforeCopy: async (prefix, ct) => { using var admission = await service.AdmitAsync(prefix, "disc.iso", ct); },
            selectPrefixLength: prefix => RomPreparationDetection.SelectPrefixLength(prefix, "disc.iso")));

        source.Position.ShouldBe(RomPreparationDetection.MaximumPrefixBytes);
        Payloads().ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateAsync_SecondPrefixSelectionExceedsBound_RejectsBeforeCallbacksOrPayload()
    {
        using var source = new GuardedPrefixInput(new byte[64]) { Admitted = true };
        int calls = 0;

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Factory().CreateAsync(source,
            includeDatHashes: _ => { calls++; return true; },
            beforeCopy: (_, _) => { calls++; return Task.CompletedTask; },
            selectPrefixLength: prefix => prefix.Length == 4 ? 32 : RomPreparationDetection.MaximumPrefixBytes + 1));

        source.Position.ShouldBe(32);
        calls.ShouldBe(0);
        Payloads().ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateAsync_TruncatedIsoDescriptor_UsesCachedIdentityHashesWithoutMediaAdmission()
    {
        byte[] bytes = SyntheticPs2Iso.Create()[..(RomPreparationDetection.MaximumPrefixBytes - 1)];
        using var source = new GuardedPrefixInput(bytes) { Admitted = true };
        var service = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => throw new InvalidOperationException("Truncated detection must not resolve media processing."), new MediaInfrastructureTestServices.EmptyReferences());
        await using var staged = await Factory(1L << 20, 2L << 20).CreateAsync(source,
            includeDatHashes: prefix => service.RequiresDatHashes(prefix, "disc.iso"),
            beforeCopy: async (prefix, ct) => (await service.AdmitAsync(prefix, "disc.iso", ct)).ShouldBeNull(),
            selectPrefixLength: prefix => RomPreparationDetection.SelectPrefixLength(prefix, "disc.iso"));
        await using var prepared = (await service.PrepareAsync(staged, "disc.iso")).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();

        File.ReadAllBytes(staged.Path).ShouldBe(bytes);
        staged.DatHashes.ShouldNotBeNull();
        prepared.Verification.Profile.ShouldBe("identity/v1");
        prepared.Hashes.BytesRead.ShouldBe(bytes.Length);
        prepared.Eligibility.ShouldBeNull();
    }

    private sealed class GuardedPrefixInput(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Admitted { get; set; }
        public int MaximumReadBytes { get; init; } = int.MaxValue;
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
        {
            if (!Admitted && Position >= 4)
                throw new InvalidOperationException("Byte five was read before media admission.");
            return base.ReadAsync(destination[..Math.Min(destination.Length, MaximumReadBytes)], ct);
        }
    }

    [Fact]
    public async Task CreateAsync_PreCopyAdmissionRejected_ReadsOnlyPrefixAndReservesNoStagingSpace()
    {
        using var source = new NonSeekableInput([82, 86, 90, 1, 2, 3, 4, 5]);
        await Should.ThrowAsync<ResourceCapacityException>(() => Factory().CreateAsync(source,
            beforeCopy: (prefix, _) =>
            {
                prefix.ToArray().ShouldBe(new byte[] { 82, 86, 90, 1 });
                Payloads().ShouldBeEmpty();
                return Task.FromException(new ResourceCapacityException("inspection occupied"));
            }));
        source.Position.ShouldBe(4);
        source.CanRead.ShouldBeTrue();
        Payloads().ShouldBeEmpty();
        File.Exists(Path.Combine(_root, "temp", "staging-v1", "reservations.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task CreateAsync_PreCopyAdmissionWait_DoesNotConsumeCopyDeadlineAndReplaysPrefix()
    {
        using var source = new NonSeekableInput([82, 86, 90, 1, 2, 3, 4, 5]);
        await using var file = await Factory(copyTimeout: 1).CreateAsync(source,
            beforeCopy: async (prefix, ct) =>
            {
                source.Position.ShouldBe(4);
                await Task.Delay(1200, ct);
            });
        File.ReadAllBytes(file.Path).ShouldBe(new byte[] { 82, 86, 90, 1, 2, 3, 4, 5 });
        source.Position.ShouldBe(8);
        file.FileSha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(new byte[] { 82, 86, 90, 1, 2, 3, 4, 5 })));
    }

    [Fact]
    public async Task CreateAsync_FileCountIsFull_EmptyFilesStillHoldSlots()
    {
        await using var first = await Factory(slots: 1).CreateAsync(new MemoryStream());
        (await Should.ThrowAsync<ResourceCapacityException>(() => Factory(slots: 1).CreateAsync(new MemoryStream())))
            .Message.ShouldContain("capacity");
    }

    [Fact]
    public async Task CreateAsync_DifferentLiveConfiguration_FailsClosed()
    {
        await using var first = await Factory().CreateAsync(new MemoryStream([1]));
        (await Should.ThrowAsync<IOException>(() => Factory(total: 32).CreateAsync(new MemoryStream([2]))))
            .Message.ShouldContain("limits differ");
        File.Exists(first.Path).ShouldBeTrue();
    }

    [Fact]
    public async Task CreateAsync_LimitsChangeWhenIdle_ReconfiguresEmptyPool()
    {
        var first = await Factory().CreateAsync(new MemoryStream([1]));
        await first.DisposeAsync();
        await using var second = await Factory(maxFile: 20, total: 32).CreateAsync(new MemoryStream(new byte[20]));
        second.FileSize.ShouldBe(20);
    }

    [Fact]
    public async Task CreateAsync_KnownOversize_RejectsWithoutPayload()
    {
        await Should.ThrowAsync<InvalidDataException>(() => Factory().CreateAsync(new MemoryStream(new byte[9])));
        Payloads().ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateAsync_UnknownOversize_RemovesPartialFileAndReservation()
    {
        await Should.ThrowAsync<InvalidDataException>(() => Factory().CreateAsync(new FaultStream(3, repeats: 3)));
        Payloads().ShouldBeEmpty();
        await using var next = await Factory().CreateAsync(new MemoryStream(new byte[8]));
    }

    [Fact]
    public async Task CreateAsync_SourceFailure_RemovesPartialFileAndReservation()
    {
        await Should.ThrowAsync<IOException>(() => Factory().CreateAsync(new FaultStream(3, fail: true)));
        Payloads().ShouldBeEmpty();
        await using var next = await Factory().CreateAsync(new MemoryStream(new byte[8]));
    }

    [Fact]
    public async Task CreateAsync_CancellationDuringCopy_RemovesPartialFileAndReservation()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new FaultStream(3, hang: true);
        Task task = Factory().CreateAsync(source, cancellation.Token);
        await source.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => task);
        Payloads().ShouldBeEmpty();
        await using var next = await Factory().CreateAsync(new MemoryStream(new byte[8]));
    }

    [Fact]
    public async Task CreateAsync_CopyDeadline_RemovesPartialFileAndReservation()
    {
        var error = await Should.ThrowAsync<InvalidDataException>(() => Factory(copyTimeout: 1).CreateAsync(new FaultStream(3, hang: true)));
        Romd.Admin.Application.Ingestion.Jobs.IngestionFailure.Classify(error)
            .ShouldBe(Romd.Admin.Application.Ingestion.Jobs.IngestionFailureKind.Permanent);
        Payloads().ShouldBeEmpty();
        await using var next = await Factory().CreateAsync(new MemoryStream(new byte[8]));
    }

    [Fact]
    public async Task CreateAsync_CancelledAdmission_DoesNotDisturbOwner()
    {
        await using var first = await Factory(slots: 1).CreateAsync(new MemoryStream([1]));
        using var cancellation = new CancellationTokenSource();
        Task waiting = Factory(slots: 1).CreateAsync(new MemoryStream([2]), cancellation.Token);
        await cancellation.CancelAsync();
        var error = await Record.ExceptionAsync(() => waiting);
        error.ShouldBeAssignableTo<OperationCanceledException>($"{error}; HResult={error?.HResult}");
        File.ReadAllBytes(first.Path).ShouldBe(new byte[] { 1 });
    }

    [Fact]
    public async Task AcquireAsync_InsufficientFreeSpace_ReservesNothing()
    {
        var pool = new TempFilePool(new FileSystem(), _root, new TempStorageOptions
        {
            MaximumBytes = 8,
            MaximumFiles = 1,
            MinimumFreeBytes = 2,
            AdmissionTimeoutSeconds = 1
        }, 8, () => 9);
        await Should.ThrowAsync<ResourceCapacityException>(() => pool.AcquireAsync(8, default));
        Directory.GetFiles(_root, "*.data").ShouldBeEmpty();
    }

    [Fact]
    public async Task AcquireAsync_DeleteFailure_KeepsAccountedCapacityUntilReclamationSucceeds()
    {
        var pool = new TempFilePool(new FileSystem(), _root, new TempStorageOptions
        {
            MaximumBytes = 8,
            MaximumFiles = 1,
            MinimumFreeBytes = 0,
            AdmissionTimeoutSeconds = 1
        }, 8, () => 100);
        var lease = await pool.AcquireAsync(8, default);
        // A directory at the payload path deterministically makes File.Delete fail on supported OSes.
        Directory.CreateDirectory(lease.Path);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => lease.DisposeAsync().AsTask());
        string before = File.ReadAllText(Path.Combine(_root, "reservations.json"));
        before.ShouldContain("\"0\":8");
        Directory.Delete(lease.Path);
        await using var recovered = await pool.AcquireAsync(8, default);
    }

    [Fact]
    public async Task EnsureCapacityAsync_UnknownLengthGrowth_ReservesBeforeWritingAndHonorsSharedBudget()
    {
        const long mib = 1L << 20;
        var pool = new TempFilePool(new FileSystem(), _root, new TempStorageOptions
        {
            MaximumBytes = 32 * mib,
            MaximumFiles = 2,
            MinimumFreeBytes = 0,
            AdmissionTimeoutSeconds = 1
        }, 32 * mib, () => 100 * mib);
        var other = await pool.AcquireAsync(20 * mib, default);
        await using var unknown = await pool.AcquireAsync(null, default);
        unknown.Reserved.ShouldBe(8 * mib);
        await Should.ThrowAsync<ResourceCapacityException>(() => unknown.EnsureCapacityAsync(9 * mib, default));
        unknown.Reserved.ShouldBe(8 * mib);
        await other.DisposeAsync();
        await unknown.EnsureCapacityAsync(9 * mib, default);
        unknown.Reserved.ShouldBe(16 * mib);
        await Should.ThrowAsync<InvalidDataException>(() => unknown.EnsureCapacityAsync(33 * mib, default));
    }

    [Fact]
    public async Task CreateAsync_ConcurrentFactories_DoNotOvercommitSharedBytes()
    {
        var attempts = Enumerable.Range(0, 12).Select(async _ =>
        {
            try
            { return await Factory(total: 16, slots: 12).CreateAsync(new MemoryStream(new byte[8])); }
            catch (ResourceCapacityException) { return null; }
        });
        var results = await Task.WhenAll(attempts);
        try
        {
            results.Count(x => x is not null).ShouldBe(2);
            Payloads().Sum(path => new FileInfo(path).Length).ShouldBe(16);
        }
        finally { foreach (var file in results.OfType<Romd.Admin.Application.Storage.Files.ITempFile>()) await file.DisposeAsync(); }
    }

    private string[] Payloads() => Directory.Exists(_root) ? Directory.GetFiles(_root, "*.data", SearchOption.AllDirectories) : [];
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class FaultStream(int count, int repeats = 1, bool fail = false, bool hang = false) : Stream
    {
        private int _reads;
        internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads++ < repeats)
            { buffer.Span[..count].Fill(7); return count; }
            if (fail)
                throw new IOException("Source failed after partial output.");
            if (hang)
            { Blocked.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
