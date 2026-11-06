using NSubstitute;
using Romd.Admin.Application.Export;
using Romd.Application.Common.Content;
using Romd.Application.Tests.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Export;

public sealed class ExportArchivePlannerTests
{
    private static readonly Sha256 Logical = Sha256.Parse(new string('a', 64));
    private static readonly Sha256 Artifact = Sha256.Parse(new string('b', 64));

    [Theory]
    [InlineData(RomDeliveryFormat.Original)]
    [InlineData(RomDeliveryFormat.Rvz)]
    public void Plan_MixedMembers_BindsSelectedBytesAndPreservesParents(RomDeliveryFormat format)
    {
        var result = ExportArchivePlanner.Plan([
            Member(1, "discs/game.iso", "game.iso", rvz: true),
            Member(2, "other/manual.bin", "manual.bin", rvz: false)
        ], format);

        result.IsError.ShouldBeFalse();
        result.Value.MissingEntries.ShouldBeEmpty();
        var game = result.Value.Entries[0];
        game.Path.ShouldBe(format == RomDeliveryFormat.Rvz ? "discs/game.rvz" : "discs/game.iso");
        var target = game.Target.ShouldNotBeNull();
        target.Format.ShouldBe(format);
        target.Sha256.ShouldBe(format == RomDeliveryFormat.Rvz ? Artifact : Logical);
        target.SizeBytes.ShouldBe(format == RomDeliveryFormat.Rvz ? 50 : 100);
        result.Value.Entries[1].Path.ShouldBe("other/manual.bin");
        result.Value.Entries[1].Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Original);
    }

    [Theory]
    [InlineData(RomDeliveryState.Unsupported)]
    [InlineData(RomDeliveryState.Preparing)]
    public void Plan_RvzUnavailableOrPreparing_SelectsAvailableOriginal(RomDeliveryState state)
    {
        var member = Member(1, "disc.iso", "disc.iso", false);
        member = member with { Formats = [member.Formats[0], new(RomDeliveryFormat.Rvz, state, null, "unknown", [])] };

        var result = ExportArchivePlanner.Plan([member], RomDeliveryFormat.Rvz);

        result.Value.Entries.ShouldHaveSingleItem().Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Original);
    }

    [Theory]
    [InlineData("../game.iso", "safe.bin")]
    [InlineData("disc/game.iso", "game.rvz")]
    [InlineData("disc/game.rvz/track.bin", "track.bin")]
    public void Plan_UnsafeOrCollidingResult_RejectsCompleteRenamedPaths(string secondPath, string secondName)
    {
        var result = ExportArchivePlanner.Plan([
            Member(1, "disc/game.iso", "game.iso", true),
            Member(2, secondPath, secondName, false)
        ], RomDeliveryFormat.Rvz);

        result.FirstError.Code.ShouldBe("Export.InvalidPaths");
    }

    [Fact]
    public void Plan_OriginalRenamingCollidesCaseInsensitively_Rejects()
    {
        var result = ExportArchivePlanner.Plan([
            Member(1, "disc/game.rvz", "game.iso", false),
            Member(2, "disc/Game.ISO", "Game.ISO", false)
        ], RomDeliveryFormat.Original);

        result.FirstError.Code.ShouldBe("Export.InvalidPaths");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plan_MissingMember_StrictFailureOrExplicitBackgroundSkip(bool skipMissing)
    {
        var result = ExportArchivePlanner.Plan([
            new(new(101, "missing.iso", 1), []),
            Member(2, "present.bin", "present.bin", false)
        ], RomDeliveryFormat.Rvz, skipMissing);

        if (!skipMissing)
            result.FirstError.Code.ShouldBe("Export.FileUnavailable");
        else
        {
            result.Value.Entries.ShouldHaveSingleItem().Path.ShouldBe("present.bin");
            result.Value.MissingEntries.ShouldHaveSingleItem().RomId.ShouldBe(1);
        }
    }

    [Fact]
    public async Task BindAsync_RepeatedMemberInDifferentParents_MeasuresOneMetadataSnapshot()
    {
        var catalog = BatchCatalog(DeliveryFacts.RvzContent(1, 101, 100, 50));

        var result = await ExportArchivePlanner.BindAsync([
            new(101, "first/game.rvz", 1), new(101, "second/game.rvz", 1)
        ], RomDeliveryFormat.Original, catalog, TestDeliveryPolicies.WithoutRuntimes, chdAdmission: Admission());

        result.Value.Entries.Select(entry => entry.Path).ShouldBe(["first/game.iso", "second/game.iso"]);
        result.Value.Entries[0].Target.ShouldBe(result.Value.Entries[1].Target);
        await catalog.Received(1).FindByRomIdsAsync(Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 1 })), CancellationToken.None);
        await catalog.DidNotReceiveWithAnyArgs().FindByRomIdAsync(default, default);
    }

    [Fact]
    public void Formats_CanonicalRvzDisc_OffersOriginalThenRvz() =>
        ExportArchivePlanner.Formats([Member(1, "Disc/game.iso", "game.iso", rvz: true)])
            .ShouldBe([RomDeliveryFormat.Original, RomDeliveryFormat.Rvz]);

    [Fact]
    public void Formats_CartridgeOnly_OffersOnlyOriginal() =>
        ExportArchivePlanner.Formats([Member(1, "game.sfc", "game.sfc", rvz: false)])
            .ShouldBe([RomDeliveryFormat.Original]);

    [Theory]
    [InlineData(RomDeliveryState.Unsupported)]
    [InlineData(RomDeliveryState.Preparing)]
    public void Formats_RvzNotAvailable_OffersOnlyOriginal(RomDeliveryState state)
    {
        var member = Member(1, "disc.iso", "disc.iso", false);
        member = member with { Formats = [member.Formats[0], new(RomDeliveryFormat.Rvz, state, null, "unknown", [])] };

        ExportArchivePlanner.Formats([member]).ShouldBe([RomDeliveryFormat.Original]);
    }

    [Fact]
    public void Formats_RvzArchiveWouldCollide_OmitsTheFormatItsArchiveRejects()
    {
        var members = new[] { Member(1, "disc/game.iso", "game.iso", true), Member(2, "disc/game.rvz", "game.rvz", false) };

        ExportArchivePlanner.Plan(members, RomDeliveryFormat.Rvz).FirstError.Code.ShouldBe("Export.InvalidPaths");
        ExportArchivePlanner.Formats(members).ShouldBe([RomDeliveryFormat.Original]);
    }

    [Fact]
    public void Formats_MissingMember_OffersOnlyOriginalAsEveryArchiveFails()
    {
        var members = new ExportArchiveMember[] { new(new(101, "missing.iso", 1), []), Member(2, "present.iso", "present.iso", true) };

        ExportArchivePlanner.Plan(members, RomDeliveryFormat.Rvz).FirstError.Code.ShouldBe("Export.FileUnavailable");
        ExportArchivePlanner.Formats(members).ShouldBe([RomDeliveryFormat.Original]);
    }

    [Fact]
    public async Task FormatsAsync_ReleaseOfMixedMembers_OffersTheUnionOfWhatItsArchivesBind()
    {
        var chd = SyntheticChdContent();
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(
            new Dictionary<int, RomContentDescriptor> { [7] = chd, [8] = RvzContent(8, 18), [9] = OriginalContent(9, 19) });
        var admission = new DeliveryAdmission(DeliveryFacts.ManagedBuild, chd.Artifacts![0].ChdEvidence![0].Decoder);
        ExportArchiveEntry[] release = [new(12, "Mixed/disc.iso", 7, PlatformKeys: ["ps2"]), new(18, "Mixed/game.iso", 8, PlatformKeys: ["ps2"]),
            new(19, "Mixed/manual.bin", 9, PlatformKeys: ["ps2"])];

        var formats = await ExportArchivePlanner.FormatsAsync([release, release[1..], release[2..]], catalog,
            TestDeliveryPolicies.WithoutRuntimes, admission);

        formats.ShouldBe([
            [RomDeliveryFormat.Original, RomDeliveryFormat.Rvz, RomDeliveryFormat.Chd],
            [RomDeliveryFormat.Original, RomDeliveryFormat.Rvz],
            [RomDeliveryFormat.Original]
        ]);
        foreach (var format in formats[0])
        {
            var bound = await ExportArchivePlanner.BindAsync(release, format, ArchiveLookups(chd), TestDeliveryPolicies.WithoutRuntimes,
                chdAdmission: admission);
            bound.Value.Entries.ShouldContain(entry => entry.Target!.Format == format);
        }
    }

    [Fact]
    public async Task FormatsAsync_ArchivesSharingMembers_ReadsEachMemberOnceInBatches()
    {
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(call =>
            call.Arg<IReadOnlyCollection<int>>().ToDictionary(id => id, id => OriginalContent(id, id + 1000)));
        var title = Enumerable.Range(1, IRomContentCatalog.MaximumBatchSize + 1)
            .Select(id => new ExportArchiveEntry(id + 1000, $"Title/file{id}.bin", id)).ToArray();

        var formats = await ExportArchivePlanner.FormatsAsync([title, title[..2]], catalog, TestDeliveryPolicies.WithoutRuntimes, Admission());

        formats.ShouldBe([[RomDeliveryFormat.Original], [RomDeliveryFormat.Original]]);
        var batches = catalog.ReceivedCalls().Select(call => (IReadOnlyCollection<int>)call.GetArguments()[0]!).ToArray();
        batches.Select(batch => batch.Count).ShouldBe([IRomContentCatalog.MaximumBatchSize, 1]);
        batches.SelectMany(batch => batch).Order().ShouldBe(Enumerable.Range(1, IRomContentCatalog.MaximumBatchSize + 1));
        await catalog.DidNotReceive().FindByRomIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FormatsAsync_MemberWithoutContentOrSafePath_OffersOnlyOriginalAsItsArchiveFails()
    {
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, RomContentDescriptor> { [8] = RvzContent(8, 18) });

        var formats = await ExportArchivePlanner.FormatsAsync([
            [new(18, "game.iso", 8), new(17, "gone.iso", 7)],
            [new(18, "../game.iso", 8)],
            [new(18, "game.iso", 8)]
        ], catalog, TestDeliveryPolicies.WithoutRuntimes, Admission());

        formats.ShouldBe([[RomDeliveryFormat.Original], [RomDeliveryFormat.Original], [RomDeliveryFormat.Original, RomDeliveryFormat.Rvz]]);
    }

    [Theory]
    [InlineData(new string[0], new[] { RomDeliveryFormat.Original })]
    [InlineData(new[] { "snes" }, new[] { RomDeliveryFormat.Original })]
    [InlineData(new[] { "wii", "gc", "snes" }, new[] { RomDeliveryFormat.Original, RomDeliveryFormat.Rvz })]
    [InlineData(new[] { "ps2", "psx", "gc" }, new[] { RomDeliveryFormat.Original, RomDeliveryFormat.Rvz, RomDeliveryFormat.Chd })]
    public void StoredFormats_Systems_OffersOriginalThenEachSystemsStoredFormatInFormatOrder(string[] systems,
        RomDeliveryFormat[] expected) =>
        ExportArchivePlanner.StoredFormats(systems, TestDeliveryPolicies.WithoutRuntimes).ShouldBe(expected);

    private static RomContentDescriptor OriginalContent(int romId, int fileId) => DeliveryFacts.OriginalContent(romId, fileId, 100);
    private static RomContentDescriptor RvzContent(int romId, int fileId) => DeliveryFacts.RvzContent(romId, fileId, 100, 50);

    /// <summary>Archive binding sees the same retained content as format discovery, through bounded batch reads.</summary>
    private static IRomContentCatalog ArchiveLookups(RomContentDescriptor chd) =>
        BatchCatalog(chd, RvzContent(8, 18), OriginalContent(9, 19));

    private static ExportArchiveMember Member(int romId, string path, string filename, bool rvz)
    {
        var identity = DeliveryFacts.Identity(100);
        var original = DeliveryFacts.Original(romId, romId + 100, romId + 100, romId + 100, identity, true);
        var encoded = DeliveryFacts.Rvz(romId, romId + 200, romId + 200, romId + 200, identity, Artifact, 50, 1, false, null, null);
        var content = new RomContentDescriptor(romId, original.OutputId, identity, filename, rvz ? [original, encoded] : [original], [], []);
        return new(new(romId + 100, path, romId), RomDeliveryPlanner.Plan(content, filename, TestDeliveryPolicies.WithoutRuntimes, chdAdmission: Admission()));
    }
    [Fact]
    public async Task BindAsync_AdmittedPs2Chd_BindsPhysicalArtifactAndPreservesLogicalFallback()
    {
        var content = SyntheticChdContent();
        var catalog = BatchCatalog(content, OriginalContent(8, 18));
        var admission = new DeliveryAdmission(DeliveryFacts.ManagedBuild, content.Artifacts![0].ChdEvidence![0].Decoder);
        var result = await ExportArchivePlanner.BindAsync([
            new(12, "Discs/game.iso", 7, PlatformKeys: ["ps2"]), new(18, "Other/manual.bin", 8, PlatformKeys: ["ps2"])
        ], RomDeliveryFormat.Chd, catalog, TestDeliveryPolicies.WithoutRuntimes, chdAdmission: admission);

        result.IsError.ShouldBeFalse();
        result.Value.MissingEntries.ShouldBeEmpty();
        result.Value.Entries.Select(entry => entry.Path).ShouldBe(["Discs/game.chd", "Other/manual.bin"]);
        var target = result.Value.Entries[0].Target.ShouldNotBeNull();
        target.Format.ShouldBe(RomDeliveryFormat.Chd);
        target.Sha256.ShouldBe(Artifact);
        target.SizeBytes.ShouldBe(4096);
        target.ContentSha256.ShouldBe(Logical);
        target.ContentSizeBytes.ShouldBe(8192);
        target.ReadBinding.ShouldBe(content.Artifacts![0].ChdEvidence![0].Bind());
        result.Value.Entries[1].Target.ShouldNotBeNull().Format.ShouldBe(RomDeliveryFormat.Original);
    }

    /// <summary>
    ///     Bytes are admitted from retained evidence whatever the member's platforms resolve: no platform, a platform the
    ///     evidence contradicts, or platforms that disagree all bind the verified CHD.
    /// </summary>
    [Theory]
    [InlineData]
    [InlineData("psx")]
    [InlineData("ps2", "psx")]
    [InlineData(null, null)]
    public async Task BindAsync_PreferredChdWithoutAUniqueKind_BindsTheAdmittedArtifact(params string?[] platforms)
    {
        var content = SyntheticChdContent();
        var catalog = BatchCatalog(content);
        var result = await ExportArchivePlanner.BindAsync([new(12, "disc.iso", 7, PlatformKeys: platforms)],
            RomDeliveryFormat.Chd, catalog, TestDeliveryPolicies.Production, chdAdmission: new DeliveryAdmission(DeliveryFacts.ManagedBuild, content.Artifacts![0].ChdEvidence![0].Decoder));

        var target = result.Value.Entries.ShouldHaveSingleItem().Target.ShouldNotBeNull();
        target.Format.ShouldBe(RomDeliveryFormat.Chd);
        target.ReadBinding.ShouldBe(content.Artifacts![0].ChdEvidence![0].Bind());
    }

    [Fact]
    public async Task BindAsync_PreferredChdWithNoAdmission_RefusesInsteadOfFallingBack()
    {
        var catalog = BatchCatalog(SyntheticChdContent());
        var result = await ExportArchivePlanner.BindAsync([new(12, "disc.iso", 7, PlatformKeys: ["ps2"])], RomDeliveryFormat.Chd, catalog, TestDeliveryPolicies.WithoutRuntimes);
        result.FirstError.Code.ShouldBe("Export.FileUnavailable");
    }
    [Fact]
    public async Task BindAsync_MultipleBatchesAndRepeatedMembers_BoundsReadsAndPreservesInputOrder()
    {
        int count = IRomContentCatalog.MaximumBatchSize * 2 + 17;
        var content = Enumerable.Range(1, count).Select(IdentityContent).ToArray();
        var catalog = new RecordingCatalog(content);
        var entries = Enumerable.Range(1, count).Reverse()
            .Select(id => new ExportArchiveEntry(id + 100, $"title{id}/game.bin", id)).ToList();
        entries.Insert(IRomContentCatalog.MaximumBatchSize + 1, new(count + 100, "repeated/game.bin", count));
        entries.Add(new(count + 100, "last/game.bin", count));

        var result = await ExportArchivePlanner.BindAsync(entries, RomDeliveryFormat.Original, catalog, TestDeliveryPolicies.WithoutRuntimes);

        result.IsError.ShouldBeFalse();
        result.Value.MissingEntries.ShouldBeEmpty();
        result.Value.Entries.Select(entry => entry.RomId).ShouldBe(entries.Select(entry => entry.RomId));
        result.Value.Entries.Select(entry => entry.Path).ShouldBe(entries.Select(entry => entry.Path));
        result.Value.Entries.All(entry => entry.Target is { Format: RomDeliveryFormat.Original }
            && entry.Target.RomId == entry.RomId && entry.Target.FileId == entry.RomId + 100).ShouldBeTrue();
        catalog.Batches.Count.ShouldBe(3);
        catalog.Batches.All(batch => batch.Length is > 0 and <= IRomContentCatalog.MaximumBatchSize).ShouldBeTrue();
        catalog.Batches.SelectMany(batch => batch).Order().ShouldBe(Enumerable.Range(1, count));
        result.Value.Entries.Where(entry => entry.RomId == count).Select(entry => entry.Target).Distinct().Count().ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BindAsync_MissingRepeatedRom_PreservesStrictOrSkipBehavior(bool skipMissing)
    {
        var catalog = new RecordingCatalog([IdentityContent(2)]);
        ExportArchiveEntry[] entries = [new(101, "first/missing.bin", 1), new(102, "present.bin", 2), new(101, "last/missing.bin", 1)];

        var result = await ExportArchivePlanner.BindAsync(entries, RomDeliveryFormat.Original, catalog, TestDeliveryPolicies.WithoutRuntimes, skipMissing);

        catalog.Batches.ShouldHaveSingleItem().Order().ShouldBe(new[] { 1, 2 });
        if (!skipMissing)
            result.FirstError.Code.ShouldBe("Export.FileUnavailable");
        else
        {
            result.Value.Entries.ShouldHaveSingleItem().Path.ShouldBe("present.bin");
            result.Value.MissingEntries.Select(entry => entry.Path).ShouldBe(new[] { "first/missing.bin", "last/missing.bin" });
        }
    }

    [Theory]
    [InlineData(0, 1, "valid.bin")]
    [InlineData(101, null, "valid.bin")]
    [InlineData(101, 0, "valid.bin")]
    [InlineData(101, -1, "valid.bin")]
    [InlineData(101, 1, "../unsafe.bin")]
    public async Task BindAsync_InvalidMemberAfterFullBatch_RejectsBeforeCatalogReads(int fileId, int? romId, string path)
    {
        var catalog = new RecordingCatalog([]);
        var entries = Enumerable.Range(1, IRomContentCatalog.MaximumBatchSize)
            .Select(id => new ExportArchiveEntry(id, $"game{id}.bin", id)).ToList();
        entries.Add(new(fileId, path, romId));

        var result = await ExportArchivePlanner.BindAsync(entries, RomDeliveryFormat.Original, catalog, TestDeliveryPolicies.WithoutRuntimes);

        result.FirstError.Code.ShouldBe("Export.InvalidPaths");
        catalog.Batches.ShouldBeEmpty();
    }

    [Fact]
    public async Task BindAsync_InvalidFormat_RejectsBeforeCatalogReads()
    {
        var catalog = new RecordingCatalog([]);

        var result = await ExportArchivePlanner.BindAsync([new(101, "game.bin", 1)], (RomDeliveryFormat)(-1), catalog, TestDeliveryPolicies.WithoutRuntimes);

        result.FirstError.Code.ShouldBe("Delivery.InvalidFormat");
        catalog.Batches.ShouldBeEmpty();
    }

    [Fact]
    public async Task BindAsync_BatchReturnsAnotherRomIdentity_RejectsMismatchedTarget()
    {
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, RomContentDescriptor> { [1] = IdentityContent(2) });

        var result = await ExportArchivePlanner.BindAsync([new(101, "game.bin", 1)], RomDeliveryFormat.Original, catalog, TestDeliveryPolicies.WithoutRuntimes);

        result.FirstError.Code.ShouldBe("Export.FileUnavailable");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BindAsync_AlreadyCancelled_ThrowsWithoutCatalogReads(bool empty)
    {
        var catalog = new RecordingCatalog([]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        ExportArchiveEntry[] entries = empty ? [] : [new(101, "game.bin", 1)];

        await Should.ThrowAsync<OperationCanceledException>(() => ExportArchivePlanner.BindAsync(
            entries, RomDeliveryFormat.Original, catalog, TestDeliveryPolicies.WithoutRuntimes, ct: cancellation.Token));

        catalog.Batches.ShouldBeEmpty();
    }

    [Fact]
    public async Task BindAsync_CancelledAfterFirstBatch_DoesNotReadNextBatch()
    {
        using var cancellation = new CancellationTokenSource();
        int count = IRomContentCatalog.MaximumBatchSize + 1;
        var catalog = new RecordingCatalog(Enumerable.Range(1, count).Select(IdentityContent).ToArray(), cancellation.Cancel);
        var entries = Enumerable.Range(1, count).Select(id => new ExportArchiveEntry(id + 100, $"game{id}.bin", id)).ToArray();

        await Should.ThrowAsync<OperationCanceledException>(() => ExportArchivePlanner.BindAsync(
            entries, RomDeliveryFormat.Original, catalog, TestDeliveryPolicies.WithoutRuntimes, ct: cancellation.Token));

        catalog.Batches.ShouldHaveSingleItem().Length.ShouldBe(IRomContentCatalog.MaximumBatchSize);
    }

    private static RomContentDescriptor IdentityContent(int romId) =>
        DeliveryFacts.OriginalContent(romId, romId + 100, 100);

    private static IRomContentCatalog BatchCatalog(params RomContentDescriptor[] descriptors)
    {
        var catalog = Substitute.For<IRomContentCatalog>();
        catalog.FindByRomIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<int, RomContentDescriptor>)descriptors
                .Where(descriptor => call.ArgAt<IReadOnlyCollection<int>>(0).Contains(descriptor.RomId))
                .ToDictionary(descriptor => descriptor.RomId));
        return catalog;
    }

    private sealed class RecordingCatalog(IReadOnlyList<RomContentDescriptor> descriptors, Action? afterRead = null) : IRomContentCatalog
    {
        public List<int[]> Batches { get; } = [];

        public Task<RomContentDescriptor?> FindAsync(Sha256 contentSha256, CancellationToken ct = default) =>
            throw new InvalidOperationException("Export planning must read bounded batches.");

        public Task<RomContentDescriptor?> FindByRomIdAsync(int romId, CancellationToken ct = default) =>
            throw new InvalidOperationException("Export planning must read bounded batches.");

        public Task<IReadOnlyDictionary<int, RomContentDescriptor>> FindByRomIdsAsync(
            IReadOnlyCollection<int> romIds, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Batches.Add(romIds.ToArray());
            afterRead?.Invoke();
            return Task.FromResult<IReadOnlyDictionary<int, RomContentDescriptor>>(descriptors
                .Where(descriptor => romIds.Contains(descriptor.RomId)).ToDictionary(descriptor => descriptor.RomId));
        }
    }

    private static RomContentDescriptor SyntheticChdContent(int romId = 7, int fileId = 12) => DeliveryFacts.ChdContent(romId, fileId);
    private static DeliveryAdmission Admission() => new(DeliveryFacts.ManagedBuild, null);
}
