using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
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
    private readonly IReplaceDatJobCreator _jobs = Substitute.For<IReplaceDatJobCreator>();
    private static string Entry(string name, string hash = "11111111") => $"<game name='{name}'><rom name='track.bin' size='123' crc='{hash}'/></game>";
    private static string Dat(string entries) => $"<datafile><header><name>Catalog</name></header>{entries}</datafile>";
    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));
    private DatReplacementReview Service(string original)
    {
        var dat = DatFile.CreateNew("Catalog", "Catalog", DatType.Redump, "catalog.dat", 1);
        _repository.GetByIdAsync(0, Arg.Any<CancellationToken>()).Returns(dat);
        _repository.GetActiveBySourceIdAsync(0, Arg.Any<CancellationToken>()).Returns(dat);
        _storage.RetrieveByIdAsync(1, Arg.Any<CancellationToken>()).Returns(_ => Stream(original));
        var parser = new DatParser([new LogiqxDatFormat(NullLogger<LogiqxDatFormat>.Instance)]);
        return new(_repository, _storage, parser, parser, _jobs);
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
        _jobs.ReceivedCalls().ShouldBeEmpty();
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
        _jobs.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task PreviewAsync_ChangedCatalog_ReturnsBoundedExactDiffWithoutWrites()
    {
        var service = Service(Dat(Entry("Removed") + Entry("Changed")));
        using var file = Stream(Dat(Entry("Added") + Entry("Changed", "22222222")));
        var result = await service.PreviewAsync(0, file, default);
        result.IsError.ShouldBeFalse();
        var p = result.Value;
        p.EntriesAdded.ShouldBe(1); p.EntriesRemoved.ShouldBe(1); p.EntriesChanged.ShouldBe(1);
        p.FilesAdded.ShouldBe(1); p.FilesRemoved.ShouldBe(1); p.FilesChanged.ShouldBe(1); p.HashesChanged.ShouldBe(1);
        p.Changes.Select(c => c.Name).ShouldBe(["Added", "Changed", "Removed"]);
        p.ActiveSha256.ShouldNotBe(p.CandidateSha256);
        _jobs.ReceivedCalls().ShouldBeEmpty();
        _storage.ReceivedCalls().ShouldAllBe(c => c.GetMethodInfo().Name == nameof(IFileStorageService.RetrieveByIdAsync));
        _repository.ReceivedCalls().ShouldAllBe(c => c.GetMethodInfo().Name.StartsWith("Get", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviewAsync_ReorderedEntries_HasDifferentDocumentButNoEntryChanges()
    {
        var service = Service(Dat(Entry("B") + Entry("A")));
        using var file = Stream(Dat(Entry("A") + Entry("B")));
        var p = (await service.PreviewAsync(0, file, default)).Value;
        p.Unchanged.ShouldBeFalse(); p.Changes.ShouldBeEmpty();
    }

    [Fact]
    public async Task PreviewAsync_ManyChanges_HasExactTotalsAndTruncatedSortedSample()
    {
        var service = Service(Dat(Entry("Existing")));
        using var file = Stream(Dat(Entry("Existing") + string.Concat(Enumerable.Range(0, 80).Reverse().Select(i => Entry($"New {i:D2}")))));
        var p = (await service.PreviewAsync(0, file, default)).Value;
        p.EntriesAdded.ShouldBe(80); p.Changes.Count.ShouldBe(50); p.Truncated.ShouldBeTrue();
        p.Changes[0].Name.ShouldBe("New 00"); p.Changes[^1].Name.ShouldBe("New 49");
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
        _jobs.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task PreviewAsync_DuplicateEntryNames_RejectsAmbiguousIdentity()
    {
        var service = Service(Dat(Entry("A")));
        using var file = Stream(Dat(Entry("A") + Entry("A")));
        (await service.PreviewAsync(0, file, default)).IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task ApplyAsync_IdenticalOrChangedSincePreview_DoesNotCreateJob()
    {
        string original = Dat(Entry("A"));
        var service = Service(original);
        using var previewStream = Stream(original);
        var p = (await service.PreviewAsync(0, previewStream, default)).Value;
        p.Unchanged.ShouldBeTrue();
        using var same = Stream(original);
        (await service.ApplyAsync(0, same, p.ActiveSha256, p.CandidateSha256, default)).FirstError.Code.ShouldBe("DatReview.Unchanged");
        using var changed = Stream(Dat(Entry("B")));
        (await service.ApplyAsync(0, changed, p.ActiveSha256, p.CandidateSha256, default)).FirstError.Code.ShouldBe("DatReview.Stale");
        _jobs.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ApplyAsync_ReviewedDocument_CreatesJobWithExactBytesAndBaseline()
    {
        string next = Dat(Entry("B"));
        var service = Service(Dat(Entry("A")));
        using var preview = Stream(next);
        var p = (await service.PreviewAsync(0, preview, default)).Value;
        byte[]? received = null;
        _jobs.CreateAsync(0, Arg.Any<Stream>(), "reviewed-catalog.dat", null, Arg.Any<CancellationToken>()).Returns(async c =>
        {
            using var buffer = new MemoryStream();
            await c.Arg<Stream>().CopyToAsync(buffer);
            received = buffer.ToArray();
            return new ReplaceDatJobCreationResult(Guid.NewGuid(), "stable", "/jobs/test");
        });
        using var approval = Stream(next);
        (await service.ApplyAsync(0, approval, p.ActiveSha256, p.CandidateSha256, default)).IsError.ShouldBeFalse();
        received.ShouldBe(Encoding.UTF8.GetBytes(next));
    }
    [Fact]
    public async Task PreviewAsync_OverByteLimit_RejectsBeforeParsingOrCreatingJob()
    {
        var service = Service(Dat(Entry("A")));
        using var file = new MemoryStream(new byte[DatReplacementReview.MaximumBytes + 1]);
        var result = await service.PreviewAsync(0, file, default);
        result.FirstError.Description.ShouldContain("32 MiB");
        _jobs.ReceivedCalls().ShouldBeEmpty();
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
        if (!valid) return;
        result.Value.CandidateEntries.ShouldBe(1); result.Value.CandidateFiles.ShouldBe(1);
        using var details = Stream(document);
        var page = (await service.ChangesAsync(null, "Catalog", details,
            new DatChangeQuery("", result.Value.CandidateSha256, EntryName: "Unknown"), default)).Value;
        page.Files.Single().Fields.ShouldNotContain(f => f.Field == "Size" && f.After == "0");
        page.Files.Single().Fields.ShouldContain(f => f.Field == "Status" && f.After == "nodump");
    }

    [Fact]
    public async Task ChangesAsync_LargeCatalog_PagesAndFiltersEveryChangeAgainstExactDocuments()
    {
        string before = Dat(string.Concat(Enumerable.Range(0, 140).Select(i => Entry($"Disc {i:D3}"))));
        string after = Dat(string.Concat(Enumerable.Range(10, 180).Select(i => Entry($"Disc {i:D3}", i < 20 ? "22222222" : "11111111"))));
        var service = Service(before);
        using var previewStream = Stream(after);
        var preview = (await service.PreviewAsync(0, previewStream, default)).Value;
        var query = new DatChangeQuery(preview.ActiveSha256, preview.CandidateSha256);
        using var firstStream = Stream(after);
        var first = (await service.ChangesAsync(0, "Catalog", firstStream, query, default)).Value;
        first.Total.ShouldBe(70); first.Entries.Count.ShouldBe(50);
        using var secondStream = Stream(after);
        var second = (await service.ChangesAsync(0, "Catalog", secondStream, query with { Offset = 50 }, default)).Value;
        second.Total.ShouldBe(70); second.Entries.Count.ShouldBe(20);
        first.Entries.Concat(second.Entries).Select(x => x.Name).Distinct().Count().ShouldBe(70);
        using var filteredStream = Stream(after);
        var filtered = (await service.ChangesAsync(0, "Catalog", filteredStream, query with { Change = "Changed", Search = "disc 01" }, default)).Value;
        filtered.Total.ShouldBe(10); filtered.Entries.All(x => x.Change == "Changed").ShouldBeTrue();
        using var beyondStream = Stream(after);
        (await service.ChangesAsync(0, "Catalog", beyondStream, query with { Offset = 100 }, default)).Value.Entries.ShouldBeEmpty();
        using var staleStream = Stream(after.Replace("22222222", "33333333"));
        (await service.ChangesAsync(0, "Catalog", staleStream, query, default)).FirstError.Code.ShouldBe("DatReview.Stale");
        using var invalidStream = Stream(after);
        (await service.ChangesAsync(0, "Catalog", invalidStream, query with { Offset = -1 }, default)).IsError.ShouldBeTrue();
        _jobs.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ChangesAsync_FileExpansion_PagesExactSizesChecksumsAndMetadata()
    {
        string files = string.Concat(Enumerable.Range(0, 75).Select(i => $"<rom name='Track {i:D3}' size='9007199254740993' crc='11111111'/>"));
        string original = Dat($"<game name='Disc'><description>Before</description>{files}</game>");
        string candidate = original.Replace("11111111", "22222222").Replace("Before", "After");
        var service = Service(original);
        using var stream = Stream(candidate);
        var preview = (await service.PreviewAsync(0, stream, default)).Value;
        var query = new DatChangeQuery(preview.ActiveSha256, preview.CandidateSha256, EntryName: "Disc");
        using var pageStream = Stream(candidate);
        var page = (await service.ChangesAsync(0, "Catalog", pageStream, query, default)).Value;
        page.Total.ShouldBe(75); page.Files.Count.ShouldBe(50);
        page.Files.All(f => f.ChecksumsChanged).ShouldBeTrue();
        page.Files[0].Fields.Single(f => f.Field == "Crc").Before.ShouldBe("11111111");
        page.EntryFields.Single(f => f.Field == "Description").After.ShouldBe("After");
        using var nextStream = Stream(candidate);
        (await service.ChangesAsync(0, "Catalog", nextStream, query with { Offset = 50 }, default)).Value.Files.Count.ShouldBe(25);
        using var initialStream = Stream(candidate);
        var initial = (await service.PreviewInitialAsync("Catalog", initialStream, default)).Value;
        using var initialPageStream = Stream(candidate);
        var initialPage = (await service.ChangesAsync(null, "Catalog", initialPageStream,
            query with { ActiveSha256 = "", CandidateSha256 = initial.CandidateSha256 }, default)).Value;
        initialPage.Files[0].Fields.Single(f => f.Field == "Size").After.ShouldBe("9007199254740993");
        initialPage.Files[0].Change.ShouldBe("Added");
        initialPage.Files[0].ChecksumsChanged.ShouldBeFalse();
    }

    [Fact]
    public async Task ChangesAsync_TenThousandEntryFirstImport_HasBoundedFirstLastAndFilteredPages()
    {
        string document = Dat(string.Concat(Enumerable.Range(0, 10_003).Reverse().Select(i => Entry($"Disc {i:D5}"))));
        var service = Service(Dat(Entry("Unused")));
        using var previewStream = Stream(document);
        var preview = (await service.PreviewInitialAsync("Catalog", previewStream, default)).Value;
        preview.CandidateEntries.ShouldBe(10_003); preview.CandidateFiles.ShouldBe(10_003);
        var query = new DatChangeQuery("", preview.CandidateSha256);
        using var firstStream = Stream(document);
        var first = (await service.ChangesAsync(null, "Catalog", firstStream, query, default)).Value;
        first.Total.ShouldBe(10_003); first.Entries.Count.ShouldBe(50); first.Entries[0].Name.ShouldBe("Disc 00000");
        using var lastStream = Stream(document);
        var last = (await service.ChangesAsync(null, "Catalog", lastStream, query with { Offset = 10_000 }, default)).Value;
        last.Entries.Select(x => x.Name).ShouldBe(["Disc 10000", "Disc 10001", "Disc 10002"]);
        using var filteredStream = Stream(document);
        var filtered = (await service.ChangesAsync(null, "Catalog", filteredStream, query with { Search = "Disc 100", Change = "Added" }, default)).Value;
        filtered.Total.ShouldBe(3);
        _repository.ReceivedCalls().ShouldBeEmpty(); _storage.ReceivedCalls().ShouldBeEmpty(); _jobs.ReceivedCalls().ShouldBeEmpty();
    }

}
