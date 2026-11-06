using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Dat.Commands.ApplyReviewedDatReplacement;
using Romd.Domain.Jobs;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Storage.Files;
using Romd.Dat.Parsing;
using Romd.Dat.Parsing.Formats.Logiqx;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Dats;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Dats;

public sealed class DatReplacementReviewTests
{
    private readonly IDatRepository _repository = Substitute.For<IDatRepository>();
    private readonly IFileStorageService _storage = Substitute.For<IFileStorageService>();
    private static string Entry(string name, string hash = "11111111") => $"<game name='{name}'><rom name='track.bin' size='123' crc='{hash}'/></game>";
    private static string Dat(string entries) => $"<datafile><header><name>Catalog</name></header>{entries}</datafile>";
    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));
    private static DatChangePageRequest Entries(string active, string candidate, string? change = null, string? query = null,
        string? cursor = null, int? limit = null) => DatChangePageRequest.ForEntries(active, candidate, change, query, cursor, limit).Value;
    private static DatChangePageRequest Files(string active, string candidate, string entryName, string? cursor = null) =>
        DatChangePageRequest.ForFiles(active, candidate, entryName, cursor, null).Value;
    private DatReplacementReview Service(string original)
    {
        var dat = DatFile.CreateNew("Catalog", "Catalog", DatType.Redump, "catalog.dat", 1, now: DateTimeOffset.UnixEpoch);
        _repository.GetByIdAsync(0, Arg.Any<CancellationToken>()).Returns(dat);
        _repository.GetActiveBySourceIdAsync(0, Arg.Any<CancellationToken>()).Returns(dat);
        _storage.RetrieveByIdAsync(1, Arg.Any<CancellationToken>()).Returns(_ => Stream(original));
        var parser = new DatParser([new LogiqxDatFormat(NullLogger<LogiqxDatFormat>.Instance)]);
        return new(_repository, _storage, parser, parser);
    }

    private void AssertReadOnly()
    {
        _repository.ReceivedCalls().ShouldAllBe(call => call.GetMethodInfo().Name == "GetByIdAsync"
            || call.GetMethodInfo().Name == "GetActiveBySourceIdAsync");
        _storage.ReceivedCalls().ShouldAllBe(call => call.GetMethodInfo().Name == "RetrieveByIdAsync");
    }

    [Fact]
    public async Task PrepareAsync_ExactReviewedPairOwnsPrivateBytesWithoutCreatingAJob()
    {
        string original = Dat(Entry("Old"));
        string next = Dat(Entry("New"));
        var service = Service(original);
        using var previewInput = Stream(next);
        var preview = (await service.PreviewAsync(0, previewInput, default)).Value;
        byte[] callerBytes = Encoding.UTF8.GetBytes(next);
        using var candidate = new MemoryStream(callerBytes);

        var result = await service.PrepareAsync(0, candidate, preview.ActiveSha256, preview.CandidateSha256);

        using var prepared = result.Value;
        prepared.Facts.ExistingDatId.ShouldBe(0);
        prepared.Facts.SourceId.ShouldBe(0);
        prepared.Facts.ActiveFileId.ShouldBe(1);
        prepared.Facts.PlatformId.ShouldBeNull();
        prepared.Facts.Name.ShouldBe("Catalog");
        prepared.Facts.ActiveSha256.ShouldBe(preview.ActiveSha256);
        prepared.Facts.CandidateSha256.ShouldBe(preview.CandidateSha256);
        Array.Fill(callerBytes, (byte)0);
        using var read = prepared.OpenRead();
        read.CanWrite.ShouldBeFalse();
        read.ShouldBeOfType<MemoryStream>().TryGetBuffer(out _).ShouldBeFalse();
        using var reader = new StreamReader(read);
        (await reader.ReadToEndAsync()).ShouldBe(next);
        AssertReadOnly();
        prepared.Dispose();
        Should.Throw<ObjectDisposedException>(() => prepared.OpenRead());
    }

    [Fact]
    public async Task PrepareAsync_ChangedPinsPreservesStaleBeforeUnchangedError()
    {
        string original = Dat(Entry("Same"));
        var service = Service(original);
        using var previewInput = Stream(original);
        var preview = (await service.PreviewAsync(0, previewInput, default)).Value;
        using var changedPins = Stream(original);
        using var unchanged = Stream(original);

        var stale = await service.PrepareAsync(0, changedPins, new string('0', 64), preview.CandidateSha256);
        var identical = await service.PrepareAsync(0, unchanged, preview.ActiveSha256, preview.CandidateSha256);

        stale.FirstError.Code.ShouldBe("DatReview.Stale");
        identical.FirstError.Code.ShouldBe("DatReview.Unchanged");
        AssertReadOnly();
    }

    [Fact]
    public async Task PreviewInitialAsync_CompleteDocument_ReportsFirstImportWithoutReadingOrWritingState()
    {
        var service = Service(Dat(Entry("Unused")));
        using var candidate = Stream(Dat(Entry("B") + Entry("A")));
        var result = await service.PreviewInitialAsync("Catalog", candidate, default);
        result.IsError.ShouldBeFalse();
        result.Value.ActiveSha256.ShouldBeEmpty();
        result.Value.ActiveEntries.ShouldBe(0);
        result.Value.CandidateEntries.ShouldBe(2);
        result.Value.EntriesAdded.ShouldBe(2);
        result.Value.FilesAdded.ShouldBe(2);
        result.Value.Changes.Select(x => x.Name).ShouldBe(["A", "B"]);
        result.Value.CandidateSha256.Length.ShouldBe(64);
        _repository.ReceivedCalls().ShouldBeEmpty();
        _storage.ReceivedCalls().ShouldBeEmpty();
        AssertReadOnly();
    }

    [Theory]
    [InlineData("<html>download failed</html>")]
    [InlineData("<datafile><header><name>Catalog</name></header></datafile>")]
    [InlineData("<datafile><header><name>Wrong</name></header><game name='A'><rom name='track' size='1' crc='11111111'/></game></datafile>")]
    [InlineData("<datafile><header><name>Catalog</name></header><game name='A'><rom name='track' size='1' crc='invalid'/></game></datafile>")]
    public async Task PreviewInitialAsync_InvalidOrWrongIdentity_RejectsWithoutImport(string document)
    {
        var service = Service(Dat(Entry("Unused")));
        using var candidate = Stream(document);
        (await service.PreviewInitialAsync("Catalog", candidate, default)).IsError.ShouldBeTrue();
        _repository.ReceivedCalls().ShouldBeEmpty();
        _storage.ReceivedCalls().ShouldBeEmpty();
        AssertReadOnly();
    }

    [Fact]
    public async Task PreviewAsync_ChangedCatalog_ReturnsBoundedExactDiffWithoutWrites()
    {
        var service = Service(Dat(Entry("Removed") + Entry("Changed")));
        using var file = Stream(Dat(Entry("Added") + Entry("Changed", "22222222")));
        var result = await service.PreviewAsync(0, file, default);
        result.IsError.ShouldBeFalse();
        var p = result.Value;
        p.EntriesAdded.ShouldBe(1);
        p.EntriesRemoved.ShouldBe(1);
        p.EntriesChanged.ShouldBe(1);
        p.FilesAdded.ShouldBe(1);
        p.FilesRemoved.ShouldBe(1);
        p.FilesChanged.ShouldBe(1);
        p.HashesChanged.ShouldBe(1);
        p.Changes.Select(c => c.Name).ShouldBe(["Added", "Changed", "Removed"]);
        p.ActiveSha256.ShouldNotBe(p.CandidateSha256);
        AssertReadOnly();
        _storage.ReceivedCalls().ShouldAllBe(c => c.GetMethodInfo().Name == nameof(IFileStorageService.RetrieveByIdAsync));
        _repository.ReceivedCalls().ShouldAllBe(c => c.GetMethodInfo().Name.StartsWith("Get", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviewAsync_ReorderedEntries_HasDifferentDocumentButNoEntryChanges()
    {
        var service = Service(Dat(Entry("B") + Entry("A")));
        using var file = Stream(Dat(Entry("A") + Entry("B")));
        var p = (await service.PreviewAsync(0, file, default)).Value;
        p.Unchanged.ShouldBeFalse();
        p.Changes.ShouldBeEmpty();
    }

    [Fact]
    public async Task PreviewAsync_ManyChanges_HasExactTotalsAndTruncatedSortedSample()
    {
        var service = Service(Dat(Entry("Existing")));
        using var file = Stream(Dat(Entry("Existing") + string.Concat(Enumerable.Range(0, 80).Reverse().Select(i => Entry($"New {i:D2}")))));
        var p = (await service.PreviewAsync(0, file, default)).Value;
        p.EntriesAdded.ShouldBe(80);
        p.Changes.Count.ShouldBe(50);
        p.Truncated.ShouldBeTrue();
        p.Changes[0].Name.ShouldBe("New 00");
        p.Changes[^1].Name.ShouldBe("New 49");
    }

    [Theory]
    [InlineData("<html>download failed</html>")]
    [InlineData("<datafile><header><name>Catalog</name></header></datafile>")]
    [InlineData("<datafile><header><name>Catalog</name></header><game name='A'><rom name='x' size='1' crc='xyz'/></game></datafile>")]
    [InlineData("<datafile><header><name>Catalog</name></header><game name='A'><rom name='x' size='-1' crc='11111111'/></game></datafile>")]
    [InlineData("<datafile><header><name>Other</name></header><game name='A'><rom name='x' size='1' crc='11111111'/></game></datafile>")]
    [InlineData("<datafile><header><name>Catalog")]
    public async Task PreviewAsync_InvalidCandidate_ReturnsValidationWithoutJob(string candidate)
    {
        var service = Service(Dat(Entry("A")));
        using var file = Stream(candidate);
        (await service.PreviewAsync(0, file, default)).IsError.ShouldBeTrue();
        AssertReadOnly();
    }

    [Fact]
    public async Task PreviewAsync_DuplicateEntryNames_RejectsAmbiguousIdentity()
    {
        var service = Service(Dat(Entry("A")));
        using var file = Stream(Dat(Entry("A") + Entry("A")));
        (await service.PreviewAsync(0, file, default)).IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task PrepareAsync_IdenticalOrChangedSincePreview_RemainsReadOnly()
    {
        string original = Dat(Entry("A"));
        var service = Service(original);
        using var previewStream = Stream(original);
        var p = (await service.PreviewAsync(0, previewStream, default)).Value;
        p.Unchanged.ShouldBeTrue();
        using var same = Stream(original);
        (await service.PrepareAsync(0, same, p.ActiveSha256, p.CandidateSha256, default)).FirstError.Code.ShouldBe("DatReview.Unchanged");
        using var changed = Stream(Dat(Entry("B")));
        (await service.PrepareAsync(0, changed, p.ActiveSha256, p.CandidateSha256, default)).FirstError.Code.ShouldBe("DatReview.Stale");
        AssertReadOnly();
    }

    [Fact]
    public async Task Handle_ReviewedDocument_StagesJobWithExactBytesAndBaselineThroughActualOwner()
    {
        string next = Dat(Entry("B"));
        var service = Service(Dat(Entry("A")));
        using var preview = Stream(next);
        var p = (await service.PreviewAsync(0, preview, default)).Value;
        byte[]? received = null;
        var lease = Substitute.For<IJobWorkspaceLease>();
        var exclusion = Substitute.For<IJobWorkspaceExclusion>();
        exclusion.AcquireAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(lease);
        var workspace = Substitute.For<IReplaceDatWorkspacePreparation>();
        IPreparedJobWorkspace preparedWorkspace = Substitute.For<IPreparedJobWorkspace>();
        workspace.PrepareAsync(Arg.Any<JobWorkspaceInput>(), Arg.Any<Stream>(), lease, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            call.Arg<JobWorkspaceInput>().FileName.ShouldBe("reviewed-catalog.dat");
            using var buffer = new MemoryStream();
            await call.Arg<Stream>().CopyToAsync(buffer);
            received = buffer.ToArray();
            return preparedWorkspace;
        });
        var acceptance = Substitute.For<IReplaceDatJobAcceptance>();
        var work = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        var owner = new ApplyReviewedDatReplacementCommandHandler(service, exclusion, workspace, acceptance, work,
            TimeProvider.System, NullLogger<ApplyReviewedDatReplacementCommandHandler>.Instance);
        using var approval = Stream(next);

        var result = await owner.HandleAsync(new(0, approval, p.ActiveSha256, p.CandidateSha256));

        result.IsError.ShouldBeFalse();
        received.ShouldBe(Encoding.UTF8.GetBytes(next));
        await acceptance.Received(1).AddStagedAsync(Arg.Is<ReplaceDatJob>(job => job.Id == result.Value
            && job.ExistingDatId == 0 && job.SourceFilename == "reviewed-catalog.dat" && job.PlatformId == null), Arg.Any<CancellationToken>());
        await transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        AssertReadOnly();
    }
    [Fact]
    public async Task PreviewAsync_OverByteLimit_RejectsBeforeParsingOrCreatingJob()
    {
        var service = Service(Dat(Entry("A")));
        using var file = new MemoryStream(new byte[DatReplacementReview.MaximumBytes + 1]);
        var result = await service.PreviewAsync(0, file, default);
        result.FirstError.Description.ShouldContain("32 MiB");
        AssertReadOnly();
    }

    [Fact]
    public async Task PreviewAsync_DuplicateFileNames_RejectsAmbiguousDiff()
    {
        var service = Service(Dat(Entry("A")));
        using var file = Stream(Dat("<game name='A'><rom name='x' size='1' crc='11111111'/><rom name='x' size='2' crc='22222222'/></game>"));
        (await service.PreviewAsync(0, file, default)).IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task PreviewAsync_NoDumpWithoutHash_PreservesDeclaredMissingDump()
    {
        var service = Service(Dat(Entry("A")));
        using var file = Stream(Dat(Entry("A") + "<game name='Missing dump'><rom name='missing.bin' size='0' status='nodump'/></game>"));
        var result = await service.PreviewAsync(0, file, default);
        result.IsError.ShouldBeFalse();
        result.Value.EntriesAdded.ShouldBe(1);
        result.Value.FilesAdded.ShouldBe(1);
    }

    [Theory]
    [InlineData("", "nodump", true)]
    [InlineData("", "baddump", false)]
    [InlineData("size='-1'", "nodump", false)]
    [InlineData("size='invalid'", "nodump", false)]
    public async Task PreviewInitialAsync_UnknownSize_AllowedOnlyForExplicitMissingDump(string size, string status, bool valid)
    {
        var service = Service(Dat(Entry("Unused")));
        var document = Dat($"<game name='Unknown'><rom name='unknown.sfc' {size} status='{status}'/></game>");
        using var stream = Stream(document);
        var result = await service.PreviewInitialAsync("Catalog", stream, default);
        result.IsError.ShouldBe(!valid);
        if (!valid)
            return;
        result.Value.CandidateEntries.ShouldBe(1);
        result.Value.CandidateFiles.ShouldBe(1);
        using var details = Stream(document);
        var page = (await service.GetEntryChangeAsync(null, "Catalog", details, "Unknown",
            Files("", result.Value.CandidateSha256, "Unknown"), default)).Value;
        page.Files.Items.Single().Fields.ShouldNotContain(f => f.Field == "Size" && f.After == "0");
        page.Files.Items.Single().Fields.ShouldContain(f => f.Field == "Status" && f.After == "nodump");
    }

    [Fact]
    public async Task ListChangesAsync_LargeCatalog_PagesAndFiltersEveryChangeAgainstExactDocuments()
    {
        string before = Dat(string.Concat(Enumerable.Range(0, 140).Select(i => Entry($"Disc {i:D3}"))));
        string after = Dat(string.Concat(Enumerable.Range(10, 180).Select(i => Entry($"Disc {i:D3}", i < 20 ? "22222222" : "11111111"))));
        var service = Service(before);
        using var previewStream = Stream(after);
        var preview = (await service.PreviewAsync(0, previewStream, default)).Value;
        using var firstStream = Stream(after);
        var first = (await service.ListChangesAsync(0, "Catalog", firstStream, Entries(preview.ActiveSha256, preview.CandidateSha256), default)).Value;
        first.Items.Count.ShouldBe(50);
        first.HasNextPage.ShouldBeTrue();
        using var secondStream = Stream(after);
        var second = (await service.ListChangesAsync(0, "Catalog", secondStream,
            Entries(preview.ActiveSha256, preview.CandidateSha256, cursor: first.NextCursor), default)).Value;
        second.Items.Count.ShouldBe(20);
        second.HasNextPage.ShouldBeFalse();
        second.NextCursor.ShouldBeNull();
        var names = first.Items.Concat(second.Items).Select(x => x.Name).ToArray();
        names.ShouldBe(names.Order(StringComparer.Ordinal));
        names.Distinct().Count().ShouldBe(70);
        using var filteredStream = Stream(after);
        var filtered = (await service.ListChangesAsync(0, "Catalog", filteredStream,
            Entries(preview.ActiveSha256, preview.CandidateSha256, "Changed", "disc 01"), default)).Value;
        filtered.Items.Count.ShouldBe(10);
        filtered.Items.All(x => x.Change == "Changed").ShouldBeTrue();
        filtered.HasNextPage.ShouldBeFalse();
        using var staleStream = Stream(after.Replace("22222222", "33333333"));
        (await service.ListChangesAsync(0, "Catalog", staleStream, Entries(preview.ActiveSha256, preview.CandidateSha256), default))
            .FirstError.Code.ShouldBe("DatReview.Stale");
        AssertReadOnly();
    }

    [Fact]
    public async Task GetEntryChangeAsync_FileExpansion_PagesExactSizesChecksumsAndMetadata()
    {
        static string Disc(string crc, string description) => $"<game name='Disc'><description>{description}</description>" +
            string.Concat(Enumerable.Range(0, 75).Select(i => $"<rom name='Track {i:D3}' size='9007199254740993' crc='{crc}'/>")) + "</game>";
        const string same = "<game name='Same'><rom name='same.bin' size='1' crc='11111111'/></game>";
        string original = Dat(Disc("11111111", "Before") + same);
        string candidate = Dat(Disc("22222222", "After") + same);
        var service = Service(original);
        using var stream = Stream(candidate);
        var preview = (await service.PreviewAsync(0, stream, default)).Value;
        using var pageStream = Stream(candidate);
        var page = (await service.GetEntryChangeAsync(0, "Catalog", pageStream, "Disc", Files(preview.ActiveSha256, preview.CandidateSha256, "Disc"), default)).Value;
        page.Name.ShouldBe("Disc");
        page.Change.ShouldBe("Changed");
        page.Files.Items.Count.ShouldBe(50);
        page.Files.HasNextPage.ShouldBeTrue();
        page.Files.Items.All(f => f.ChecksumsChanged).ShouldBeTrue();
        page.Files.Items[0].Fields.Single(f => f.Field == "Crc").Before.ShouldBe("11111111");
        page.Fields.Single(f => f.Field == "Description").After.ShouldBe("After");
        using var nextStream = Stream(candidate);
        var next = (await service.GetEntryChangeAsync(0, "Catalog", nextStream, "Disc",
            Files(preview.ActiveSha256, preview.CandidateSha256, "Disc", page.Files.NextCursor), default)).Value;
        next.Files.Items.Count.ShouldBe(25);
        next.Files.HasNextPage.ShouldBeFalse();
        next.Files.Items[0].Name.ShouldBe("Track 050");
        // The detail is a member of the change list: unchanged and unknown entries are not found.
        using var sameStream = Stream(candidate);
        (await service.GetEntryChangeAsync(0, "Catalog", sameStream, "Same", Files(preview.ActiveSha256, preview.CandidateSha256, "Same"), default))
            .FirstError.Code.ShouldBe("DatReview.Entry");
        using var unknownStream = Stream(candidate);
        (await service.GetEntryChangeAsync(0, "Catalog", unknownStream, "Unknown", Files(preview.ActiveSha256, preview.CandidateSha256, "Unknown"), default))
            .FirstError.Code.ShouldBe("DatReview.Entry");
        using var initialStream = Stream(candidate);
        var initial = (await service.PreviewInitialAsync("Catalog", initialStream, default)).Value;
        using var initialPageStream = Stream(candidate);
        var initialPage = (await service.GetEntryChangeAsync(null, "Catalog", initialPageStream, "Disc",
            Files("", initial.CandidateSha256, "Disc"), default)).Value;
        initialPage.Change.ShouldBe("Added");
        initialPage.Files.Items[0].Fields.Single(f => f.Field == "Size").After.ShouldBe("9007199254740993");
        initialPage.Files.Items[0].Change.ShouldBe("Added");
        initialPage.Files.Items[0].ChecksumsChanged.ShouldBeFalse();
    }

    [Fact]
    public async Task ListChangesAsync_TenThousandEntryFirstImport_HasBoundedFirstLastAndFilteredPages()
    {
        string document = Dat(string.Concat(Enumerable.Range(0, 10_003).Reverse().Select(i => Entry($"Disc {i:D5}"))));
        var service = Service(Dat(Entry("Unused")));
        using var previewStream = Stream(document);
        var preview = (await service.PreviewInitialAsync("Catalog", previewStream, default)).Value;
        preview.CandidateEntries.ShouldBe(10_003);
        preview.CandidateFiles.ShouldBe(10_003);
        using var firstStream = Stream(document);
        var first = (await service.ListChangesAsync(null, "Catalog", firstStream, Entries("", preview.CandidateSha256), default)).Value;
        first.Items.Count.ShouldBe(50);
        first.Items[0].Name.ShouldBe("Disc 00000");
        first.HasNextPage.ShouldBeTrue();
        // Twenty-three names contain "100"; pages of five end on the last three entries of the document.
        string? cursor = null;
        var pages = new List<string[]>();
        do
        {
            using var pageStream = Stream(document);
            var page = (await service.ListChangesAsync(null, "Catalog", pageStream,
                Entries("", preview.CandidateSha256, "Added", "100", cursor, 5), default)).Value;
            pages.Add(page.Items.Select(x => x.Name).ToArray());
            cursor = page.NextCursor;
        } while (cursor is not null);
        pages.Count.ShouldBe(5);
        pages[^1].ShouldBe(["Disc 10000", "Disc 10001", "Disc 10002"]);
        using var filteredStream = Stream(document);
        var filtered = (await service.ListChangesAsync(null, "Catalog", filteredStream, Entries("", preview.CandidateSha256, "Added", "Disc 100"), default)).Value;
        filtered.Items.Count.ShouldBe(3);
        filtered.HasNextPage.ShouldBeFalse();
        _repository.ReceivedCalls().ShouldBeEmpty();
        _storage.ReceivedCalls().ShouldBeEmpty();
        AssertReadOnly();
    }

}
