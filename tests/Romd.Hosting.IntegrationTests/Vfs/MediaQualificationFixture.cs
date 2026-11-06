using System.Globalization;
using System.Runtime.ExceptionServices;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using Romd.Admin.Application.Export;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Delivery;
using Romd.Storage;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Vfs;

public sealed class MediaQualificationFactAttribute : FactAttribute
{
    public MediaQualificationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ROMD_MEDIA_QUALIFICATION_FIXTURES")))
            Skip = "Set ROMD_MEDIA_QUALIFICATION_FIXTURES and a new ROMD_MEDIA_DELIVERY_REPORT to run real-disc delivery qualification.";
    }
}

internal sealed record MediaQualificationInput(string Name, string Iso, string NormalizedRvz,
    string Dat, string DatId, string NintendoKind)
{
    public string SystemKey => NintendoKind.Equals("GameCube", StringComparison.OrdinalIgnoreCase) ? "gc" : "wii";
}

internal sealed record MediaQualificationFacts(long Size, string Sha256, string Md5, string Sha1, string Crc32)
{
    public static MediaQualificationFacts From(VfsProbeContent content) => new(content.Length,
        Convert.ToHexStringLower(content.Sha256), Convert.ToHexStringLower(content.Md5),
        Convert.ToHexStringLower(content.Sha1), Convert.ToHexStringLower(content.Crc32));
}

internal sealed record MediaQualificationDat(string GameName, string RomName, long Size,
    string Md5, string Sha1, string Crc32);
internal sealed record MediaQualificationAccess(long CasOpens, long CasReads, long CasBytes,
    long CasMetadata, long DecoderOpens, long DecodedReads, long DecodedBytes, long ResolverOpens);

internal sealed record MediaQualificationConnectionProgress(string Alias, string AccountAlias, string Format,
    int ControlledHeldLeases, long VerifiedRangeBytes);
internal sealed record MediaQualificationContention(string Stage, int GlobalLimit, int AccountLimit,
    int ConnectionLimit, int QueueWaitMilliseconds, int MaximumHeldOwnerLeases, int MaximumObservedQueued,
    double? CancelledRequestMilliseconds, double? TimeoutRequestMilliseconds, double? GrantedRequestMilliseconds,
    IReadOnlyList<MediaQualificationConnectionProgress> Connections, MediaQualificationCleanupEvidence? Cleanup = null);
internal sealed record MediaQualificationCleanupEvidence(string State, bool UnjoinedRequest,
    int FailureCount, string? FailureType);

internal sealed class MediaQualificationCounters
{
    private long _casOpens, _casReads, _casBytes, _casMetadata;
    private long _decoderOpens, _decodedReads, _decodedBytes, _resolverOpens;
    public MediaQualificationAccess Snapshot() => new(Interlocked.Read(ref _casOpens),
        Interlocked.Read(ref _casReads), Interlocked.Read(ref _casBytes), Interlocked.Read(ref _casMetadata),
        Interlocked.Read(ref _decoderOpens), Interlocked.Read(ref _decodedReads),
        Interlocked.Read(ref _decodedBytes), Interlocked.Read(ref _resolverOpens));
    public void CasOpen() => Interlocked.Increment(ref _casOpens);
    public void CasMetadata() => Interlocked.Increment(ref _casMetadata);
    public void Decode() => Interlocked.Increment(ref _decoderOpens);
    public void Resolve() => Interlocked.Increment(ref _resolverOpens);
    public void Read(int bytes, bool decoded)
    {
        if (decoded)
        { Interlocked.Increment(ref _decodedReads); Interlocked.Add(ref _decodedBytes, bytes); }
        else
        { Interlocked.Increment(ref _casReads); Interlocked.Add(ref _casBytes, bytes); }
    }
}

internal static class MediaQualificationFixture
{
    public const int MaximumConfigurationBytes = 64 * 1024;
    public const long MaximumContentBytes = 16L << 30;

    public static ConsumerDeliveryOptions CreateDeliveryOptions() => new()
    {
        SigningKeyId = "media-qualification-" + Guid.NewGuid().ToString("N"),
        SigningSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(
            ConsumerDeliveryOptions.MinimumSigningSecretBytes)),
        SignedUrlTtlMinutes = 2
    };

    public static void Require(bool condition, string operation)
    {
        if (!condition)
            throw new InvalidDataException("Media qualification failed: " + operation);
    }

    public static async Task<IReadOnlyList<MediaQualificationInput>> LoadAsync(
        string configPath, string reportPath, CancellationToken ct)
    {
        Require(Path.IsPathFullyQualified(configPath) && File.Exists(configPath), "absolute fixture configuration");
        Require(Path.IsPathFullyQualified(reportPath) && !File.Exists(reportPath) && !Directory.Exists(reportPath),
            "new absolute report path");
        Require(new FileInfo(configPath).Length is > 0 and <= MaximumConfigurationBytes, "bounded fixture configuration");
        await using var input = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] bytes = new byte[MaximumConfigurationBytes + 1];
        int size = 0;
        while (size < bytes.Length)
        {
            int read = await input.ReadAsync(bytes.AsMemory(size), ct);
            if (read == 0)
                break;
            size += read;
        }
        Require(size <= MaximumConfigurationBytes, "configuration growth bound");
        using var document = JsonDocument.Parse(bytes.AsMemory(0, size), new JsonDocumentOptions { MaxDepth = 4 });
        Require(document.RootElement.ValueKind == JsonValueKind.Array, "fixture array");
        var values = document.RootElement.EnumerateArray().ToArray();
        Require(values.Length is >= 1 and <= 2, "one or two real disc fixtures");
        var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MediaQualificationInput>(values.Length);
        foreach (var value in values)
        {
            ct.ThrowIfCancellationRequested();
            Require(value.ValueKind == JsonValueKind.Object, "fixture object");
            Require(value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count()
                == value.EnumerateObject().Count(), "unique configuration fields");
            string Field(string name)
            {
                Require(value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String,
                    "required fixture field");
                string text = field.GetString()!;
                Require(!string.IsNullOrWhiteSpace(text), "nonempty fixture field");
                return text;
            }
            var fixture = new MediaQualificationInput(Field("Name"), Field("Iso"), Field("NormalizedRvz"),
                Field("Dat"), Field("DatId"), Field("NintendoKind"));
            Require(fixture.Name.Length <= 128 && !fixture.Name.Any(char.IsControl) && names.Add(fixture.Name),
                "bounded distinct fixture names");
            Require(fixture.NintendoKind.Equals("GameCube", StringComparison.OrdinalIgnoreCase) ||
                fixture.NintendoKind.Equals("Wii", StringComparison.OrdinalIgnoreCase), "supported Nintendo disc kind");
            Require(kinds.Add(fixture.NintendoKind), "distinct Nintendo disc kinds");
            Require(fixture.DatId.Length <= 20 && ulong.TryParse(fixture.DatId, NumberStyles.None,
                CultureInfo.InvariantCulture, out var datId) && datId != 0, "positive external DAT identity");
            foreach (string path in new[] { fixture.Iso, fixture.NormalizedRvz, fixture.Dat })
            {
                Require(Path.IsPathFullyQualified(path) && File.Exists(path) && !Directory.Exists(path),
                    "existing absolute fixture file");
                var file = new FileInfo(path);
                Require(file.LinkTarget is null, "immutable fixture is a regular file");
                string full = Path.GetFullPath(path);
                Require(paths.Add(full) || path == fixture.Dat, "distinct immutable media paths");
                Require(file.Length is > 0 and <= MaximumContentBytes, "bounded fixture file");
                string report = Path.GetFullPath(reportPath);
                Require(full != report && !report.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !full.StartsWith(report + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                    "report does not alias immutable fixtures");
            }
            result.Add(fixture);
        }
        Require(Path.GetFullPath(configPath) != Path.GetFullPath(reportPath), "report does not alias configuration");
        return result.AsReadOnly();
    }

    public static async Task<MediaQualificationDat> ReadDatAsync(MediaQualificationInput fixture,
        MediaQualificationFacts original, CancellationToken ct)
    {
        await using var stream = new FileStream(fixture.Dat, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var xml = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = 256L << 20,
            MaxCharactersFromEntities = 1024
        });
        MediaQualificationDat? matched = null;
        while (await xml.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "game" ||
                xml.GetAttribute("id") != fixture.DatId)
                continue;
            Require(matched is null, "unique DAT game identity");
            string gameName = xml.GetAttribute("name") ?? "";
            Require(gameName.Length is > 0 and <= 500, "DAT game name");
            using var game = xml.ReadSubtree();
            int roms = 0;
            while (await game.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (game.NodeType != XmlNodeType.Element || game.LocalName != "rom")
                    continue;
                Require(++roms == 1, "single disc DAT requirement");
                string name = game.GetAttribute("name") ?? "";
                Require(ExportArchivePaths.TryNormalize(name, out _), "portable DAT requirement path");
                Require(long.TryParse(game.GetAttribute("size"), NumberStyles.None, CultureInfo.InvariantCulture,
                    out long size), "DAT size");
                matched = new(gameName, name, size, (game.GetAttribute("md5") ?? "").ToLowerInvariant(),
                    (game.GetAttribute("sha1") ?? "").ToLowerInvariant(),
                    (game.GetAttribute("crc") ?? "").ToLowerInvariant());
            }
            Require(roms == 1, "DAT contains one disc");
        }
        Require(matched is not null && matched.Size == original.Size && matched.Md5 == original.Md5 &&
            matched.Sha1 == original.Sha1 && matched.Crc32 == original.Crc32, "ISO matches all Redump DAT facts");
        return matched!;
    }

    public static async Task<MediaQualificationFacts> MeasureAsync(Stream stream, long maximumBytes, CancellationToken ct)
    {
        Require(maximumBytes is >= 0 and <= MaximumContentBytes, "bounded stream measurement");
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var crc = new System.IO.Hashing.Crc32();
        byte[] buffer = new byte[64 * 1024];
        long size = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int count = await stream.ReadAsync(buffer, ct);
            if (count == 0)
                break;
            size = checked(size + count);
            Require(size <= maximumBytes, "stream exceeds advertised byte bound");
            sha256.AppendData(buffer, 0, count);
            sha1.AppendData(buffer, 0, count);
            md5.AppendData(buffer, 0, count);
            crc.Append(buffer.AsSpan(0, count));
        }
        return new(size, Convert.ToHexStringLower(sha256.GetHashAndReset()),
            Convert.ToHexStringLower(md5.GetHashAndReset()), Convert.ToHexStringLower(sha1.GetHashAndReset()),
            Romd.Domain.Hashing.Crc32.FromLittleEndian(crc.GetCurrentHash()).ToString());
    }

    public static async Task<MediaQualificationFacts> MeasureFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await MeasureAsync(stream, stream.Length, ct);
    }

    public static async Task ValidateZipAsync(string path,
        IReadOnlyDictionary<string, MediaQualificationFacts> expected, CancellationToken ct)
    {
        Require(expected.Count is >= 1 and <= 4, "bounded expected archive members");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        Require(archive.Entries.Count == expected.Count, "exact archive member count");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            Require(ExportArchivePaths.TryNormalize(entry.FullName, out _) && names.Add(entry.FullName),
                "safe unique archive paths");
            Require(expected.TryGetValue(entry.FullName, out var facts), "exact archive member name");
            Require(entry.Length == facts!.Size, "archive member length");
            await using var content = await entry.OpenAsync(ct);
            Require(await MeasureAsync(content, facts.Size, ct) == facts, "archive member matches delivered bytes");
        }
    }

    public static async Task ExecuteWithRevocationAsync(Func<CancellationToken, Task> action,
        HttpClient owner, Guid connectionId, TimeSpan cleanupTimeout, CancellationToken ct)
    {
        Require(cleanupTimeout > TimeSpan.Zero && cleanupTimeout <= TimeSpan.FromSeconds(30),
            "positive revocation deadline no longer than thirty seconds");
        Exception? primary = null;
        try
        { await action(ct); }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            using var deadline = new CancellationTokenSource(cleanupTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete,
                    "/api/me/connections/" + connectionId.ToString("D"));
                using var response = await owner.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                Require(response.StatusCode == System.Net.HttpStatusCode.NoContent, "revoke owned qualification connection");
            }
            catch (Exception failure) when (primary is not null)
            { TryAttachCleanupDiagnostic(primary, "MediaQualification.RevocationFailureType", failure.GetType().FullName); }
        }
    }

    public static async Task CancelAndDisposeResponseAsync(Task<HttpResponseMessage> pending,
        CancellationTokenSource requestCancellation, CancellationToken ct)
    {
        Exception? failure = null;
        try
        { await requestCancellation.CancelAsync(); }
        catch (Exception cancellationFailure) { failure = cancellationFailure; }
        try
        {
            // Request cancellation stops the HTTP work; caller cancellation cannot abandon its owned response.
            using var response = await pending;
        }
        catch (OperationCanceledException) when (pending.IsCanceled && requestCancellation.IsCancellationRequested) { }
        catch (Exception requestFailure)
        { failure = failure is null ? requestFailure : new AggregateException(failure, requestFailure); }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        ct.ThrowIfCancellationRequested();
    }

    public static MediaQualificationCleanupEvidence ReleaseContentLeasesAfterJoin(IReadOnlyList<IDisposable> held, Task? pending,
        Exception? primary, Exception? cleanupFailure)
    {
        var failures = new List<Exception>();
        if (cleanupFailure is not null)
            failures.Add(cleanupFailure);
        bool joined = pending is null || pending.IsCompleted;
        if (!joined)
            failures.Add(new InvalidDataException("Media qualification failed: unjoined HTTP request keeps content leases pinned"));
        else
            foreach (var lease in held)
                try
                { lease.Dispose(); }
                catch (Exception failure) { failures.Add(failure); }
        if (failures.Count == 0)
            return new("completed", false, 0, null);
        Exception observed = failures.Count == 1 ? failures[0] : new AggregateException(failures);
        var reported = primary ?? observed;
        TryAttachCleanupDiagnostic(reported, "MediaQualification.PendingCleanupFailureType", observed.GetType().FullName);
        TryAttachCleanupDiagnostic(reported, "MediaQualification.PendingCleanupFailureCount", failures.Count);
        TryAttachCleanupDiagnostic(reported, "MediaQualification.UnjoinedRequest", !joined);
        if (primary is null)
            ExceptionDispatchInfo.Capture(observed).Throw();
        return new("failed", !joined, failures.Count, observed.GetType().FullName);
    }

    public static void TryAttachCleanupDiagnostic(Exception primary, string key, object? value)
    {
        try
        { primary.Data[key] = value; }
        catch (Exception) { /* Diagnostic attachment cannot replace the primary qualification failure. */ }
    }

    public static object? ReadCleanupDiagnostic(Exception? primary, string key)
    {
        try
        { return primary?.Data[key]; }
        catch (Exception) { return null; }
    }

    public static async Task<byte[]> ReadSmallAsync(Stream input, int maximumBytes, CancellationToken ct)
    {
        Require(maximumBytes is >= 1 and <= MaximumConfigurationBytes, "bounded metadata response");
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int count = await input.ReadAsync(buffer, ct);
            if (count == 0)
                return output.ToArray();
            Require(output.Length + count <= maximumBytes, "metadata response bound");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
    }
}

internal sealed class MediaQualificationCountedContent(IRandomAccessContent inner,
    MediaQualificationCounters counters, bool decoded = false) : IRandomAccessContent
{
    public long Length => inner.Length;
    public ContentReadIntegrity Integrity => inner.Integrity;
    public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
    {
        int count = await inner.ReadAtAsync(offset, destination, ct);
        counters.Read(count, decoded);
        return count;
    }
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

internal sealed class MediaQualificationCountedStream(Stream inner, MediaQualificationCounters counters) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        counters.Read(read, false);
        return read;
    }
    public override int Read(Span<byte> buffer)
    {
        int read = inner.Read(buffer);
        counters.Read(read, false);
        return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int read = await inner.ReadAsync(buffer, ct);
        counters.Read(read, false);
        return read;
    }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        await ReadAsync(buffer.AsMemory(offset, count), ct);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
}

internal sealed class MediaQualificationStore(IContentAddressableStore inner, MediaQualificationCounters counters,
    bool ownsInner) : IContentAddressableStore
{
    public Task<IStagedContent> StageAsync(Stream content, StorageKey? expectedKey = null,
        IProgress<StoreProgress>? progress = null, CancellationToken ct = default) => inner.StageAsync(content, expectedKey, progress, ct);
    public Task<StoreResult> StoreAsync(Stream content, IProgress<StoreProgress>? progress = null,
        CancellationToken ct = default) => inner.StoreAsync(content, progress, ct);
    public async Task<Stream?> RetrieveAsync(StorageKey key, CancellationToken ct = default)
    {
        counters.CasOpen();
        var stream = await inner.RetrieveAsync(key, ct);
        return stream is null ? null : new MediaQualificationCountedStream(stream, counters);
    }
    public Task<bool> VerifyAsync(StorageKey key, CancellationToken ct = default)
    { counters.CasOpen(); return inner.VerifyAsync(key, ct); }
    public Task<int> CleanupTemporaryFilesAsync(CancellationToken ct = default) => inner.CleanupTemporaryFilesAsync(ct);
    public ISynchronousRawContent? OpenSynchronousRaw(StorageKey key)
    {
        counters.CasOpen();
        return inner.OpenSynchronousRaw(key);
    }
    public async Task<IRandomAccessContent?> OpenRandomAccessAsync(StorageKey key, CancellationToken ct = default)
    {
        counters.CasOpen();
        var content = await inner.OpenRandomAccessAsync(key, ct);
        return content is null ? null : new MediaQualificationCountedContent(content, counters);
    }
    public Task<bool> ExistsAsync(StorageKey key, CancellationToken ct = default)
    { counters.CasMetadata(); return inner.ExistsAsync(key, ct); }
    public IAsyncEnumerable<StorageKey> EnumerateAsync(CancellationToken ct = default)
    { counters.CasMetadata(); return inner.EnumerateAsync(ct); }
    public Task<bool> DeleteAsync(StorageKey key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
    public Task<ContentInfo?> GetInfoAsync(StorageKey key, CancellationToken ct = default)
    { counters.CasMetadata(); return inner.GetInfoAsync(key, ct); }
    public ValueTask DisposeAsync() => ownsInner ? inner.DisposeAsync() : ValueTask.CompletedTask;
}
