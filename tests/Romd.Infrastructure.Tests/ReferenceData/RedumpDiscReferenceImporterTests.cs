using Romd.Domain.Source.Rom;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Infrastructure.ReferenceData;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

/// <remarks>Synthetic internal handlers qualify orchestration and shape, never authenticated HTTPS.
/// The fake staging port enforces its contract, not PostgreSQL transaction/grant/immutability behavior.</remarks>
public sealed class RedumpDiscReferenceImporterTests
{
    private static readonly Guid Identity = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset RetrievedAt = DateTimeOffset.UnixEpoch.AddTicks(1234);

    [Theory]
    [InlineData("DVD-5", RomDiscReferenceMedia.Dvd5)]
    [InlineData("DVD-9", RomDiscReferenceMedia.Dvd9)]
    public async Task PrepareAsync_ExplicitCanonicalPage_BindsExactBodyQuartetAndRetrievalWithoutStaging(
        string media, RomDiscReferenceMedia kind)
    {
        byte[] raw = Encoding.UTF8.GetBytes(Page(media));
        using var session = new Session(raw);
        session.Clock.Now = RetrievedAt.ToOffset(TimeSpan.FromHours(-5));
        var prepared = await session.Importer.PrepareAsync(42, Identity);
        prepared.IsError.ShouldBeFalse();
        var record = prepared.Value.Record;
        record.Id.ShouldBe(Identity);
        record.System.ShouldBe(RomDiscReferenceSystem.PlayStation2);
        record.Media.ShouldBe(kind);
        record.FileCount.ShouldBe(1);
        record.LogicalBytes.ShouldBe(1048576);
        record.Crc32.ShouldBe(Crc32.FromUInt32(42));
        record.Md5.ShouldBe(Md5.Parse(new string('1', 32)));
        record.Sha1.ShouldBe(Sha1.Parse(new string('2', 40)));
        record.RawReferenceBytes.ShouldBe(raw.Length);
        record.RawReferenceSha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(raw)));
        record.ReferenceSource.ShouldBe("https://redump.info/disc/42");
        record.ParserProfile.ShouldBe(RedumpDiscMediaReferenceParser.Profile);
        record.AcceptedSourceProvenance.ShouldBe(RedumpDiscReferenceFetcher.Profile);
        record.RetrievedAt.ShouldBe(RetrievedAt);
        prepared.Value.CopyRawBody().ShouldBe(raw);
        session.Handler.Requests.ShouldBe(new[] { "GET https://redump.info/disc/42" });
        session.Store.Calls.ShouldBe(0);
        session.Store.HasTransaction.ShouldBeFalse();
    }

    [Fact]
    public async Task PrepareAsync_NoCallerIdentity_AssignsDistinctIdentityPerRetrieval()
    {
        using var session = new Session();
        var first = (await session.Importer.PrepareAsync(42)).Value;
        var second = (await session.Importer.PrepareAsync(42)).Value;
        first.Record.Id.ShouldNotBe(Guid.Empty);
        second.Record.Id.ShouldNotBe(first.Record.Id);
        first.Record.RawReferenceSha256.ShouldBe(second.Record.RawReferenceSha256);
        session.Store.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PrepareAsync_InvalidDiscId_DoesNotFetchOrStage(int discId)
    {
        using var session = new Session();
        (await session.Importer.PrepareAsync(discId, Identity)).FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.InvalidDiscId());
        session.Handler.Requests.ShouldBeEmpty();
        session.Store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task PrepareAsync_EmptyCallerIdentity_DoesNotFetchOrStage()
    {
        using var session = new Session();
        (await session.Importer.PrepareAsync(42, Guid.Empty)).FirstError.ShouldBe(RomDiscMediaReferenceImportErrors.InvalidIdentity());
        session.Handler.Requests.ShouldBeEmpty();
        session.Store.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task PrepareAsync_UnavailableFetch_PreservesErrorWithoutRetryOrStaging(int status)
    {
        using var session = new Session(send: (request, _) => Task.FromResult(Response(request, Encoding.UTF8.GetBytes(Page()), status)));
        (await session.Importer.PrepareAsync(42, Identity)).FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.Unavailable());
        session.Handler.Requests.Count.ShouldBe(1);
        session.Store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task PrepareAsync_OverLimitResponse_RefusesBeforeParsingOrStaging()
    {
        byte[] raw = new byte[(1 << 20) + 1];
        using var session = new Session(send: (request, _) => Task.FromResult(Response(request, raw)));
        (await session.Importer.PrepareAsync(42, Identity)).FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.InputLimit());
        session.Handler.Requests.Count.ShouldBe(1);
        session.Store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task PrepareAsync_UnexpectedFetcherFault_PropagatesOriginalExceptionWithoutStaging()
    {
        var expected = new InvalidOperationException("synthetic transport logic fault");
        using var session = new Session(send: (_, _) => throw expected);
        Exception? observed = null;
        try
        { await session.Importer.PrepareAsync(42, Identity); }
        catch (Exception failure) { observed = failure; }
        observed.ShouldBeSameAs(expected);
        session.Handler.Requests.Count.ShouldBe(1);
        session.Store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task PrepareAsync_ForeignFinalUri_RefusesAdmissionEvenWithValidHtml()
    {
        using var foreignRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.test/disc/42");
        using var session = new Session(send: (_, _) => Task.FromResult(Response(foreignRequest, Encoding.UTF8.GetBytes(Page()))));
        (await session.Importer.PrepareAsync(42, Identity)).FirstError.ShouldBe(RedumpDiscReferenceFetchErrors.ResponseSource());
        session.Handler.Requests.Count.ShouldBe(1);
        session.Store.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("cd")]
    [InlineData("malformed")]
    [InlineData("utf8")]
    public async Task PrepareAsync_RefusedSourceOrPage_NeverStagesAReference(string failure)
    {
        byte[] raw = failure switch
        {
            "cd" => Encoding.UTF8.GetBytes(Page("CD")),
            "malformed" => Encoding.UTF8.GetBytes(Page().Replace("<th>Size</th>", "<th>Other</th>", StringComparison.Ordinal)),
            "utf8" => [255, 255],
            _ => Encoding.UTF8.GetBytes(Page())
        };
        using var session = new Session(send: (request, _) => Task.FromResult(Response(request, raw, failure == "redirect" ? 302 : 200)));
        var result = await session.Importer.PrepareAsync(42, Identity);
        var expected = failure switch
        {
            "redirect" => RedumpDiscReferenceFetchErrors.RefusedStatus(),
            "cd" => RedumpDiscMediaReferenceErrors.Unsupported(),
            "utf8" => RedumpDiscReferenceFetchErrors.InvalidUtf8(),
            _ => RedumpDiscMediaReferenceErrors.Malformed()
        };
        result.FirstError.ShouldBe(expected);
        session.Handler.Requests.Count.ShouldBe(1);
        session.Store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task StageAsync_PreparedBeforeTransaction_PreservesExactRecordAndCopySafeBody()
    {
        using var session = new Session();
        var prepared = (await session.Importer.PrepareAsync(42, Identity)).Value;
        byte[] expected = prepared.CopyRawBody();
        byte[] callerSnapshot = prepared.CopyRawBody();
        callerSnapshot[0] ^= 127;
        session.Store.HasTransaction.ShouldBeFalse();
        session.Store.Calls.ShouldBe(0);
        var result = await StageInCallerTransaction(session, prepared);
        result.Value.ShouldBe(prepared.Record);
        session.Store.Rows[Identity].Record.ShouldBe(prepared.Record);
        session.Store.Rows[Identity].Body.ShouldBe(expected);
        session.Handler.Requests.Count.ShouldBe(1);
        session.Store.Calls.ShouldBe(1);
        session.Store.HasTransaction.ShouldBeFalse();
    }

    [Fact]
    public async Task StageAsync_SamePreparedRetrievalReplay_ReusesGuidWithoutFetchingOrReplacing()
    {
        using var session = new Session();
        var prepared = (await session.Importer.PrepareAsync(42, Identity)).Value;
        (await StageInCallerTransaction(session, prepared)).Value.ShouldBe(prepared.Record);
        (await StageInCallerTransaction(session, prepared)).Value.ShouldBe(prepared.Record);
        session.Store.Rows.Count.ShouldBe(1);
        session.Store.Rows[Identity].Body.ShouldBe(prepared.CopyRawBody());
        session.Store.Calls.ShouldBe(2);
        session.Handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("timestamp")]
    public async Task StageAsync_ChangedRetrievalReusesGuid_ConflictsWithoutReplacingOriginal(string change)
    {
        byte[] raw = Encoding.UTF8.GetBytes(Page());
        using var session = new Session(send: (request, _) => Task.FromResult(Response(request, raw)));
        var first = (await session.Importer.PrepareAsync(42, Identity)).Value;
        (await StageInCallerTransaction(session, first)).IsError.ShouldBeFalse();
        if (change == "body")
            raw = Encoding.UTF8.GetBytes(Page().Replace("synthetic.iso", "renamed.iso", StringComparison.Ordinal));
        else
            session.Clock.Now = RetrievedAt.AddTicks(1);
        var changed = (await session.Importer.PrepareAsync(42, Identity)).Value;
        (await StageInCallerTransaction(session, changed)).FirstError.ShouldBe(RomDiscMediaReferenceImportErrors.ConflictingIdentity());
        session.Store.Rows.Count.ShouldBe(1);
        session.Store.Rows[Identity].Record.ShouldBe(first.Record);
        session.Store.Rows[Identity].Body.ShouldBe(first.CopyRawBody());
        session.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task StageAsync_NewRetrievalSameQuartet_RetainsContradictoryMediaAndProvenance()
    {
        byte[] raw = Encoding.UTF8.GetBytes(Page());
        using var session = new Session(send: (request, _) => Task.FromResult(Response(request, raw)));
        var first = (await session.Importer.PrepareAsync(42)).Value;
        (await StageInCallerTransaction(session, first)).IsError.ShouldBeFalse();
        raw = Encoding.UTF8.GetBytes(Page("DVD-9"));
        session.Clock.Now = RetrievedAt.AddTicks(1);
        var second = (await session.Importer.PrepareAsync(42)).Value;
        (await StageInCallerTransaction(session, second)).IsError.ShouldBeFalse();
        second.Record.Id.ShouldNotBe(first.Record.Id);
        second.Record.Matches(new(first.Record.LogicalBytes, first.Record.Sha1, first.Record.Md5, first.Record.Crc32)).ShouldBeTrue();
        second.Record.Media.ShouldNotBe(first.Record.Media);
        session.Store.Rows.Count.ShouldBe(2);
        session.Store.Rows[first.Record.Id].Record.ShouldBe(first.Record);
        session.Store.Rows[second.Record.Id].Record.ShouldBe(second.Record);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("tuple")]
    [InlineData("timestamp")]
    [InlineData("body-hash")]
    [InlineData("provenance")]
    [InlineData("system")]
    [InlineData("media")]
    [InlineData("file-count")]
    [InlineData("sha1")]
    [InlineData("md5")]
    [InlineData("crc32")]
    [InlineData("raw-size")]
    [InlineData("source")]
    [InlineData("profile")]
    public async Task StageAsync_ForeignSuccessfulRecord_RefusesPortContractViolation(string change)
    {
        using var session = new Session();
        var prepared = (await session.Importer.PrepareAsync(42, Identity)).Value;
        session.Store.Callback = (record, _, _) => Task.FromResult(ErrorOrFactory.From(CopyRecord(record, change)));
        (await StageInCallerTransaction(session, prepared)).FirstError.ShouldBe(RomDiscMediaReferenceImportErrors.InvalidStagedResult());
        session.Store.Calls.ShouldBe(1);
        session.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task StageAsync_StoreUnavailable_PreservesBusinessErrorWithoutRetry()
    {
        using var session = new Session();
        var prepared = (await session.Importer.PrepareAsync(42, Identity)).Value;
        session.Store.Callback = (_, _, _) => Task.FromResult<ErrorOr<RomDiscMediaReferenceRecord>>(RomDiscMediaReferenceImportErrors.Unavailable());
        (await StageInCallerTransaction(session, prepared)).FirstError.ShouldBe(RomDiscMediaReferenceImportErrors.Unavailable());
        session.Store.Calls.ShouldBe(1);
        session.Store.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task StageAsync_UnexpectedStoreFailure_PropagatesOriginalException()
    {
        using var session = new Session();
        var prepared = (await session.Importer.PrepareAsync(42, Identity)).Value;
        var expected = new InvalidOperationException("synthetic staging logic fault");
        session.Store.Callback = (_, _, _) => throw expected;
        Exception? observed = null;
        try
        { await StageInCallerTransaction(session, prepared); }
        catch (Exception failure) { observed = failure; }
        observed.ShouldBeSameAs(expected);
        session.Store.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareOrStage_PreCancelled_PropagatesCallerTokenBeforeNextSideEffect(bool stage)
    {
        using var session = new Session();
        var prepared = stage ? (await session.Importer.PrepareAsync(42, Identity)).Value : null;
        int sendsBefore = session.Handler.Requests.Count;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await AssertCancellation(stage
            ? session.Importer.StageAsync(prepared!, cancellation.Token)
            : session.Importer.PrepareAsync(42, Identity, cancellation.Token), cancellation.Token);
        session.Handler.Requests.Count.ShouldBe(sendsBefore);
        session.Store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task PrepareAsync_CancellationDuringFetch_PreservesCallerTokenAndNeverStages()
    {
        using var cancellation = new CancellationTokenSource();
        using var session = new Session(send: (_, ct) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(ct);
        });
        await AssertCancellation(session.Importer.PrepareAsync(42, Identity, cancellation.Token), cancellation.Token);
        session.Store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task StageAsync_CancellationAfterPortReturns_CannotReportSuccess()
    {
        using var session = new Session();
        var prepared = (await session.Importer.PrepareAsync(42, Identity)).Value;
        using var cancellation = new CancellationTokenSource();
        session.Store.Callback = (record, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(ErrorOrFactory.From(record));
        };
        session.Store.HasTransaction = true;
        await AssertCancellation(session.Importer.StageAsync(prepared, cancellation.Token), cancellation.Token);
        session.Store.Calls.ShouldBe(1);
        // The real caller must roll back any staged row; the importer owns no database commit.
    }

    [Fact]
    public async Task StageAsync_NoCallerTransaction_PropagatesPortAdmissionFailureWithoutFetching()
    {
        using var session = new Session();
        var prepared = (await session.Importer.PrepareAsync(42, Identity)).Value;
        await Should.ThrowAsync<InvalidOperationException>(() => session.Importer.StageAsync(prepared));
        session.Store.Calls.ShouldBe(0);
        session.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Dispose_InternalBorrowedClient_DisablesImporterWithoutDisposingCallerClient()
    {
        using var session = new Session();
        session.Importer.Dispose();
        await Should.ThrowAsync<ObjectDisposedException>(() => session.Importer.PrepareAsync(42));
        session.Handler.Requests.ShouldBeEmpty();
        using var response = await session.Client.GetAsync("https://redump.info/disc/42");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        session.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Dispose_OwnedClient_ReleasesItAndRefusesFurtherPreparation()
    {
        using var session = new Session();
        using var importer = new RedumpDiscReferenceImporter(new(session.Client, session.Clock), session.Store, session.Client);
        importer.Dispose();
        importer.Dispose();
        await Should.ThrowAsync<ObjectDisposedException>(() => importer.PrepareAsync(42));
        await Should.ThrowAsync<ObjectDisposedException>(() => session.Client.GetAsync("https://redump.info/disc/42"));
        session.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void PreparedEnvelope_InputAndReturnedCopyMutation_DoNotChangeBoundBody()
    {
        byte[] raw = Encoding.UTF8.GetBytes(Page());
        var record = RedumpDiscMediaReferenceParser.Parse(raw, Identity, "https://redump.info/disc/42", 42,
            RetrievedAt, RedumpDiscReferenceFetcher.Profile).Value;
        var prepared = new RedumpDiscReferenceImport(record, raw);
        byte[] expected = raw.ToArray();
        raw[0] ^= 127;
        byte[] copy = prepared.CopyRawBody();
        copy[0] ^= 63;
        prepared.CopyRawBody().ShouldBe(expected);
        prepared.Record.ShouldBe(record);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedEnvelope_WrongLengthOrBytes_RefusesSeparatedRecordAndBody(bool wrongLength)
    {
        byte[] raw = Encoding.UTF8.GetBytes(Page());
        var record = RedumpDiscMediaReferenceParser.Parse(raw, Identity, "https://redump.info/disc/42", 42,
            RetrievedAt, RedumpDiscReferenceFetcher.Profile).Value;
        if (wrongLength)
            raw = raw[..^1];
        else
            raw[0] ^= 127;
        Should.Throw<ArgumentException>(() => new RedumpDiscReferenceImport(record, raw));
    }

    private static RomDiscMediaReferenceRecord CopyRecord(RomDiscMediaReferenceRecord record, string change) => new(
        change == "id" ? Guid.NewGuid() : record.Id,
        change == "system" ? RomDiscReferenceSystem.PlayStation1 : record.System,
        change == "media" ? RomDiscReferenceMedia.Cd : record.Media,
        change == "file-count" ? record.FileCount + 1 : record.FileCount,
        change == "tuple" ? record.LogicalBytes + 2048 : record.LogicalBytes,
        change == "sha1" ? Sha1.Parse(new string('3', 40)) : record.Sha1,
        change == "md5" ? Md5.Parse(new string('4', 32)) : record.Md5,
        change == "crc32" ? Crc32.FromUInt32(43) : record.Crc32,
        change == "raw-size" ? record.RawReferenceBytes + 1 : record.RawReferenceBytes,
        change == "body-hash" ? Sha256.Parse(new string('a', 64)) : record.RawReferenceSha256,
        change == "source" ? "https://redump.info/disc/43" : record.ReferenceSource,
        change == "profile" ? "different-parser/v1" : record.ParserProfile,
        change == "provenance" ? "different-provenance/v1" : record.AcceptedSourceProvenance,
        change == "timestamp" ? record.RetrievedAt.AddTicks(1) : record.RetrievedAt);

    private static async Task<ErrorOr<RomDiscMediaReferenceRecord>> StageInCallerTransaction(Session session,
        RedumpDiscReferenceImport prepared)
    {
        session.Store.HasTransaction = true;
        try
        { return await session.Importer.StageAsync(prepared); }
        finally { session.Store.HasTransaction = false; }
    }

    private static async Task AssertCancellation(Task operation, CancellationToken expected)
    {
        OperationCanceledException? observed = null;
        try
        { await operation; }
        catch (OperationCanceledException failure) { observed = failure; }
        if (observed is null)
            throw new InvalidOperationException("Expected caller cancellation.");
        observed.CancellationToken.ShouldBe(expected);
    }

    private static string Page(string media = "DVD-5") =>
        "<!DOCTYPE html><html><body><table class='disc-info-table'><tr><td>System</td><td>Sony PlayStation 2</td></tr>" +
        "<tr><td>Media</td><td>" + media + "</td></tr></table><table class='files-table'><thead><tr>" +
        "<th>Filename</th><th>Size</th><th>CRC-32</th><th>MD5</th><th>SHA-1</th></tr></thead><tbody><tr>" +
        "<td>synthetic.iso</td><td>1048576</td><td>0000002a</td><td>11111111111111111111111111111111</td>" +
        "<td>2222222222222222222222222222222222222222</td></tr></tbody></table></body></html>";

    private static HttpResponseMessage Response(HttpRequestMessage request, byte[] raw, int status = 200)
    {
        var content = new ByteArrayContent(raw);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-8" };
        return new((HttpStatusCode)status) { RequestMessage = request, Content = content };
    }

    private sealed class Session : IDisposable
    {
        public Session(byte[]? raw = null, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null)
        {
            raw ??= Encoding.UTF8.GetBytes(Page());
            Handler = new(send ?? ((request, _) => Task.FromResult(Response(request, raw))));
            Client = new(Handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            Importer = new(new RedumpDiscReferenceFetcher(Client, Clock), Store);
        }
        public Handler Handler { get; }
        public HttpClient Client { get; }
        public MutableClock Clock { get; } = new();
        public StagingStore Store { get; } = new();
        public RedumpDiscReferenceImporter Importer { get; }
        public void Dispose() { Importer.Dispose(); Client.Dispose(); Handler.Dispose(); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.Method + " " + request.RequestUri?.OriginalString);
            return send(request, ct);
        }
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = RetrievedAt;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StagingStore : IRomDiscMediaReferenceImportStore
    {
        public bool HasTransaction { get; set; }
        public int Calls { get; private set; }
        public Dictionary<Guid, (RomDiscMediaReferenceRecord Record, byte[] Body)> Rows { get; } = [];
        public Func<RomDiscMediaReferenceRecord, ReadOnlyMemory<byte>, CancellationToken,
            Task<ErrorOr<RomDiscMediaReferenceRecord>>>? Callback
        { get; set; }
        public Task<string> GetRuntimeRoleAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("This importer-only fake has no runtime-role admission query.");

        public Task<ErrorOr<RomDiscMediaReferenceRecord>> StageAsync(RomDiscMediaReferenceRecord record,
            ReadOnlyMemory<byte> rawBody, CancellationToken ct = default)
        {
            if (!HasTransaction)
                throw new InvalidOperationException("The caller must open its transaction before staging.");
            ct.ThrowIfCancellationRequested();
            Calls++;
            if (Callback is not null)
                return Callback(record, rawBody, ct);
            if (Rows.TryGetValue(record.Id, out var existing))
            {
                if (existing.Record != record || !rawBody.Span.SequenceEqual(existing.Body))
                    return Task.FromResult<ErrorOr<RomDiscMediaReferenceRecord>>(RomDiscMediaReferenceImportErrors.ConflictingIdentity());
                return Task.FromResult(ErrorOrFactory.From(existing.Record));
            }
            Rows.Add(record.Id, (record, rawBody.ToArray()));
            return Task.FromResult(ErrorOrFactory.From(record));
        }
    }
}
