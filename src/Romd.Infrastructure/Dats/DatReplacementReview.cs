using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using ErrorOr;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Dat.Parsing;
using Romd.Domain.Source.Dat;

namespace Romd.Infrastructure.Dats;

/// <summary>
/// Read-only, bounded review of original documents. The browser retains the candidate;
/// approval resubmits and revalidates it. No candidate claims or CAS rows are created here.
/// </summary>
public sealed partial class DatReplacementReview : IDatReplacementReview
{
    private readonly IDatRepository _repository;
    private readonly IFileStorageService _storage;
    private readonly IDatHeaderReader _headers;
    private readonly IDatGameStreamReader _games;
    private readonly IReplaceDatJobCreator _jobs;

    public DatReplacementReview(IDatRepository repository, IFileStorageService storage,
        IDatHeaderReader headers, IDatGameStreamReader games, IReplaceDatJobCreator jobs)
    {
        _repository = repository;
        _storage = storage;
        _headers = headers;
        _games = games;
        _jobs = jobs;
    }

    public const int MaximumBytes = 32 * 1024 * 1024;
    private const int MaximumEntries = 100_000;
    private const int MaximumFiles = 500_000;
    private const int SampleLimit = 50;
    private sealed record FileFact(string Content, string Hashes);
    private sealed record Entry(bool Bios, string Metadata, Dictionary<string, FileFact> Files);
    private sealed record Document(byte[] Bytes, string Hash, string Name, string? Version, Dictionary<string, Entry> Entries);
    private sealed class InvalidDocumentException(string message) : Exception(message);

    public async Task<ErrorOr<DatDocumentInspection>> InspectAsync(Stream candidate, CancellationToken ct)
    {
        try
        {
            var document = await ParseAsync(candidate, ct);
            return new DatDocumentInspection(document.Name, document.Bytes.Length,
                Compare(new Document([], "", document.Name, null, []), document));
        }
        catch (Exception ex) when (ex is InvalidDocumentException or XmlException)
        {
            return Error.Validation("DatReview.InvalidDocument", ex is InvalidDocumentException ? ex.Message : "The DAT contains malformed XML. No catalog has been imported.");
        }
    }

    public async Task<ErrorOr<DatReplacementPreview>> PreviewInitialAsync(string expectedName, Stream candidate, CancellationToken ct)
    {
        try
        {
            var document = await ParseAsync(candidate, ct);
            if (!string.Equals(document.Name, expectedName, StringComparison.Ordinal))
                return Error.Validation("DatReview.Identity", "This document does not match the selected catalog.");
            // Empty identity denotes the explicit absence of an installed catalog;
            // it is never accepted by the replacement activation path.
            return Compare(new Document([], "", expectedName, null, []), document);
        }
        catch (Exception ex) when (ex is InvalidDocumentException or XmlException)
        {
            return Error.Validation("DatReview.InvalidDocument", ex is InvalidDocumentException ? ex.Message : "The DAT contains malformed XML. No catalog has been imported.");
        }
    }

    public async Task<ErrorOr<DatReplacementPreview>> PreviewAsync(int datId, Stream candidate, CancellationToken ct)
    {
        var pair = await ReadAsync(datId, candidate, ct);
        return pair.IsError ? pair.Errors : Compare(pair.Value.Active, pair.Value.Candidate);
    }

    public async Task<ErrorOr<ReplaceDatJobCreationResult>> ApplyAsync(int datId, Stream candidate,
        string activeHash, string candidateHash, CancellationToken ct)
    {
        var pair = await ReadAsync(datId, candidate, ct);
        if (pair.IsError) return pair.Errors;
        var (active, next) = pair.Value;
        if (active.Hash != activeHash || next.Hash != candidateHash)
            return Error.Conflict("DatReview.Stale", "The catalog or selected file changed. Preview it again before applying.");
        if (active.Hash == next.Hash)
            return Error.Conflict("DatReview.Unchanged", "This document is already active. No replacement is needed.");
        // ExistingDatId is the durable baseline identity. The executor passes it to the
        // activation transaction, which rejects a baseline that changes while queued.
        await using var stream = new MemoryStream(next.Bytes, writable: false);
        return await _jobs.CreateAsync(datId, stream, "reviewed-catalog.dat", null, ct);
    }

    private async Task<ErrorOr<(Document Active, Document Candidate)>> ReadAsync(int datId, Stream candidate, CancellationToken ct)
    {
        var dat = await _repository.GetByIdAsync(datId, ct);
        if (dat is null) return Error.NotFound("DatReview.NotFound", "The catalog no longer exists.");
        var active = await _repository.GetActiveBySourceIdAsync(dat.DatSourceId, ct);
        if (active?.Id != datId || dat.Lifecycle != DatFileLifecycle.Active)
            return Error.Conflict("DatReview.Stale", "The active catalog changed. Open the current version and preview again.");
        await using var original = await _storage.RetrieveByIdAsync(dat.FileId, ct);
        if (original is null) return Error.NotFound("DatReview.DocumentMissing", "The original catalog document is unavailable.");
        try
        {
            var before = await ParseAsync(original, ct);
            var after = await ParseAsync(candidate, ct);
            if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal))
                return Error.Validation("DatReview.Identity", "The DAT header names differ. Choose an update for this exact catalog.");
            return (before, after);
        }
        catch (Exception ex) when (ex is InvalidDocumentException or XmlException)
        {
            return Error.Validation("DatReview.InvalidDocument", ex is InvalidDocumentException ? ex.Message : "The DAT contains malformed XML. The active catalog has not changed.");
        }
    }

    private async Task<Document> ParseAsync(Stream input, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        while (true)
        {
            int read = await input.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > MaximumBytes) throw new InvalidDocumentException("Review supports DAT documents up to 32 MiB.");
            buffer.Write(chunk, 0, read);
        }
        byte[] raw = buffer.ToArray();
        using var stream = new MemoryStream(raw, writable: false);
        // The ingest parser is deliberately tolerant. Review must reject declarations
        // it would otherwise skip or normalize to missing hashes/zero sizes.
        var declarations = ValidateXml(stream, ct);
        stream.Position = 0;
        var header = await _headers.ReadHeaderAsync(stream, ct);
        if (header.IsError) throw new InvalidDocumentException("The DAT header could not be read.");
        stream.Position = 0;
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        int fileCount = 0;
        await foreach (var result in _games.StreamGamesAsync(stream, ct))
        {
            if (result.IsError) throw new InvalidDocumentException("A catalog entry could not be parsed.");
            var game = result.Value;
            var files = new Dictionary<string, FileFact>(StringComparer.Ordinal);
            foreach (var rom in game.Roms)
            {
                var facts = JsonSerializer.Serialize(rom);
                if (declarations.UnknownSizes.Contains((game.Name, rom.Name)))
                {
                    var fields = JsonNode.Parse(facts)!;
                    fields["Size"] = null;
                    facts = fields.ToJsonString();
                }
                if (!files.TryAdd("rom:" + rom.Name, new(facts, JsonSerializer.Serialize(new { rom.Crc, rom.Md5, rom.Sha1 }))))
                    throw new InvalidDocumentException("Duplicate file names within an entry cannot be reviewed safely.");
            }
            foreach (var disk in game.Disks)
                if (!files.TryAdd("disk:" + disk.Name, new(JsonSerializer.Serialize(disk), JsonSerializer.Serialize(new { disk.Md5, disk.Sha1 }))))
                    throw new InvalidDocumentException("Duplicate disk names within an entry cannot be reviewed safely.");
            fileCount += files.Count;
            if (entries.Count >= MaximumEntries || fileCount > MaximumFiles)
                throw new InvalidDocumentException("This catalog exceeds the review limit of 100,000 entries or 500,000 files.");
            string metadata = JsonSerializer.Serialize(new { game.Description, game.Year, game.Manufacturer, game.CloneOf, game.RomOf, game.IsBios, game.NameMetadata });
            if (!entries.TryAdd(game.Name, new(game.IsBios, metadata, files)))
                throw new InvalidDocumentException("Duplicate entry names cannot be reviewed safely.");
        }
        if (entries.Count != declarations.Entries || fileCount != declarations.Files)
            throw new InvalidDocumentException("The parser could not preserve every declared entry and file.");
        if (entries.Count == 0 || fileCount == 0) throw new InvalidDocumentException("An empty catalog cannot replace the working catalog.");
        return new(raw, Convert.ToHexStringLower(SHA256.HashData(raw)), header.Value.Name, header.Value.Version, entries);
    }

    private static (int Entries, int Files, HashSet<(string Entry, string File)> UnknownSizes) ValidateXml(Stream stream, CancellationToken ct)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, CloseInput = false,
            MaxCharactersInDocument = MaximumBytes
        });
        int headerCount = 0, entries = 0, files = 0;
        var unknownSizes = new HashSet<(string Entry, string File)>();
        string? entryName = null;
        bool root = false;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.NamespaceURI.Length != 0) throw new InvalidDocumentException("Namespaced XML is not supported for review.");
            if (reader.Depth == 0)
            {
                root = reader.Name == "datafile";
                if (!root) throw new InvalidDocumentException("Choose an extracted Logiqx DAT/XML document, not a ZIP or download page.");
            }
            if (reader.Name == "header")
            {
                if (reader.Depth != 1 || ++headerCount > 1) throw new InvalidDocumentException("Exactly one catalog header is required.");
            }
            if (reader.Name is "game" or "machine")
            {
                if (reader.Depth != 1 || string.IsNullOrWhiteSpace(reader.GetAttribute("name")) || ++entries > MaximumEntries)
                    throw new InvalidDocumentException("Invalid or excessive catalog entries.");
                entryName = reader.GetAttribute("name");
            }
            if (reader.Name is not ("rom" or "disk")) continue;
            if (reader.Depth != 2 || string.IsNullOrWhiteSpace(reader.GetAttribute("name")) || ++files > MaximumFiles)
                throw new InvalidDocumentException("Invalid or excessive file declarations.");
            // Official No-Intro nodump records can omit size. Keep their status and
            // original document; a present but malformed size is still invalid.
            var size = reader.GetAttribute("size");
            bool unknownSize = size is null && reader.GetAttribute("status") == "nodump";
            if (reader.Name == "rom" && unknownSize && entryName is not null)
                unknownSizes.Add((entryName, reader.GetAttribute("name")!));
            if (reader.Name == "rom" && !unknownSize && !long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw new InvalidDocumentException("Dumped ROMs need a valid non-negative size; only nodump records may omit it.");
            bool hasHash = false;
            foreach (var (name, length) in new[] { ("crc", 8), ("md5", 32), ("sha1", 40) })
            {
                string? value = reader.GetAttribute(name);
                if (value is null) continue;
                if (value.Length != length || value.Any(c => !Uri.IsHexDigit(c)))
                    throw new InvalidDocumentException("A file has an invalid checksum.");
                if (reader.Name != "disk" || name != "crc") hasHash = true;
            }
            if (!hasHash && reader.GetAttribute("status") != "nodump")
                throw new InvalidDocumentException("Every dumped file needs a valid checksum; missing dumps must declare nodump.");
        }
        if (!root || headerCount != 1) throw new InvalidDocumentException("A Logiqx catalog with one header is required.");
        return (entries, files, unknownSizes);
    }

    private static DatReplacementPreview Compare(Document before, Document after)
    {
        int added = 0, removed = 0, changed = 0, fa = 0, fr = 0, fc = 0, hashes = 0;
        var samples = new List<DatEntryChange>();
        foreach (var item in EntryChanges(before, after, CancellationToken.None))
        {
            if (item.Change == "Added") added++;
            else if (item.Change == "Removed") removed++;
            else changed++;
            fa += item.FilesAdded; fr += item.FilesRemoved; fc += item.FilesChanged;
            if (before.Entries.TryGetValue(item.Name, out var old) && after.Entries.TryGetValue(item.Name, out var next))
                hashes += next.Files.Count(file => old.Files.TryGetValue(file.Key, out var prior) && file.Value.Hashes != prior.Hashes);
            if (samples.Count < SampleLimit) samples.Add(item);
        }
        return new(before.Hash, after.Hash, before.Version, after.Version, before.Hash == after.Hash,
            before.Entries.Count, after.Entries.Count, added, removed, changed, fa, fr, fc, hashes,
            before.Entries.Values.Count(e => e.Bios), after.Entries.Values.Count(e => e.Bios), samples,
            added + removed + changed > SampleLimit, before.Entries.Values.Sum(e => e.Files.Count), after.Entries.Values.Sum(e => e.Files.Count));
    }
}
