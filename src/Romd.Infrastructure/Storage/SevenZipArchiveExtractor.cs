using System.Buffers;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using Romd.Admin.Application.Ingestion.Extraction;
using Romd.Domain.Jobs;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;
using ArchiveEntry = Romd.Admin.Application.Ingestion.Extraction.ArchiveEntry;

namespace Romd.Infrastructure.Storage;

/// <summary>Managed, bounded 7z extraction. Solid folders are read once in archive order.</summary>
public sealed class SevenZipArchiveExtractor : IArchiveExtractor
{
    private const int BufferSize = 64 * 1024;
    public IReadOnlyList<string> SupportedExtensions { get; } = [".7z"];

    // Route split names here as well so they fail visibly instead of being ingested as ROMs.
    public bool CanHandle(string filename) => Path.GetExtension(filename).Equals(".7z", StringComparison.OrdinalIgnoreCase)
        || IsVolume(filename);

    private static bool IsVolume(string filename)
    {
        int marker = filename.LastIndexOf(".7z.", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 && filename.Length > marker + 4 && filename[(marker + 4)..].All(char.IsAsciiDigit);
    }

    public async IAsyncEnumerable<ArchiveEntry> EnumerateAsync(Stream archiveStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var archive = Open(archiveStream, cancellationToken);
        foreach (var entry in archive.Entries.Cast<SevenZipArchiveEntry>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsAnti)
                continue;
            yield return new ArchiveEntry(entry.Key!, 0, entry.Size, entry.IsDirectory);
        }
        await Task.CompletedTask;
    }

    public async Task<Stream> ExtractAsync(Stream archiveStream, string entryPath,
        CancellationToken cancellationToken = default)
    {
        using var archive = Open(archiveStream, cancellationToken);
        if (!archive.Entries.Any(e => e.Key == entryPath && !e.IsDirectory && !((SevenZipArchiveEntry)e).IsAnti))
            throw new FileNotFoundException($"Entry '{entryPath}' not found in archive");
        // No production caller currently uses single-entry extraction. Return an independent,
        // seekable disk stream; deletion on disposal avoids the ZIP implementation's full RAM copy.
        var output = new FileStream(Path.Combine(Path.GetTempPath(), $"romd-7z-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            using var reader = archive.ExtractAllEntries();
            while (Decode(reader.MoveToNextEntry))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory || ((SevenZipArchiveEntry)reader.Entry).IsAnti)
                    continue;
                bool selected = reader.Entry.Key == entryPath;
                await CopyEntryAsync(reader, selected ? output : Stream.Null, archive.Crcs[reader.Entry], cancellationToken);
                if (!selected)
                    continue;
                output.Position = 0;
                return output;
            }
            throw new FileNotFoundException($"Entry '{entryPath}' not found in archive");
        }
        catch
        {
            await output.DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<ExtractedEntry>> ExtractAllAsync(Stream archiveStream, string targetDirectory,
        IExtractionQuota quota, CancellationToken cancellationToken = default)
    {
        using var archive = Open(archiveStream, cancellationToken);
        // Claims track explicit entries separately from implicit parents. An explicit directory
        // may follow a child, but repeated explicit entries and case aliases always collide.
        var paths = new OutputPaths(targetDirectory, quota);
        paths.CreateRoot();
        var result = new List<ExtractedEntry>();
        using var reader = archive.ExtractAllEntries();
        while (Decode(reader.MoveToNextEntry))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = (SevenZipArchiveEntry)reader.Entry;
            if (entry.IsAnti)
                continue;
            string relative = NormalizePath(entry.Key!);
            string absolute = paths.Claim(relative, entry.IsDirectory, entry.Key!);
            if (entry.IsDirectory)
            {
                paths.CreateDirectory(relative, entry.Key!);
                result.Add(new ExtractedEntry(relative, absolute, 0, true));
                continue;
            }
            string? parent = Path.GetDirectoryName(relative);
            if (!string.IsNullOrEmpty(parent))
                paths.CreateDirectory(parent.Replace('\\', '/'), entry.Key!);
            if (Path.Exists(absolute))
                throw new ArchiveEntryCollisionException(entry.Key!);
            ChargeEntry(quota);
            await using var output = new QuotaEnforcingStream(OpenNew(absolute, entry.Key!), quota);
            long written = await CopyEntryAsync(reader, output, archive.Crcs[reader.Entry], cancellationToken);
            result.Add(new ExtractedEntry(relative, absolute, written, false));
        }
        return result;
    }

    private static OpenedArchive Open(Stream source, CancellationToken ct)
    {
        if (!source.CanSeek)
            throw new ArgumentException("Archive stream must be seekable", nameof(source));
        ct.ThrowIfCancellationRequested();
        if (source is FileStream file && IsVolume(file.Name))
            throw new InvalidDataException("Multi-volume 7z archives are unsupported.");
        return Decode(() =>
        {
            var expectedCrcs = SevenZipHeaderGuard.Validate(source, ct);
            source.Position = 0;
            var archive = SevenZipArchive.OpenArchive(source, new ReaderOptions { LeaveStreamOpen = true });
            try
            {
                // Force metadata loading inside the error boundary, before creating any output.
                foreach (var entry in archive.Entries.Cast<SevenZipArchiveEntry>())
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.IsAnti)
                        continue;
                    NormalizePath(entry.Key ?? throw new InvalidDataException("Missing 7z entry name."));
                    // Encryption was rejected by the guard. SharpCompress 0.50.4 reports empty
                    // files as encrypted because their folder is null; do not use that flag.
                    if (entry.Size < 0)
                        throw new InvalidDataException("Invalid 7z entry size.");
                    // SevenZipEntry.LinkTarget is always null. POSIX type bits live in ExtendedAttrib.
                    // Never apply those attributes: links and special files are rejected, not created.
                    uint attributes = unchecked((uint)(entry.ExtendedAttrib ?? 0));
                    uint type = (attributes >> 16) & 0xf000;
                    if ((attributes & (uint)FileAttributes.ReparsePoint) != 0 || type is not (0 or 0x8000 or 0x4000))
                        throw new InvalidDataException("7z links and special files are unsupported.");
                }
                return new OpenedArchive(archive, expectedCrcs);
            }
            catch { archive.Dispose(); throw; }
        });
    }

    private static async Task<long> CopyEntryAsync(IReader reader, Stream output, uint expectedCrc, CancellationToken ct)
    {
        using var input = Decode(reader.OpenEntryStream);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var crc = new Crc32();
        long written = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int count = Decode(() => input.Read(buffer, 0, BufferSize));
                if (count == 0)
                    break;
                crc.Append(buffer.AsSpan(0, count));
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
                written = checked(written + count);
            }
            // The library's solid reader exposes a sized substream without an entry CRC wrapper.
            // Verify CRC ourselves. The header guard requires a CRC for each payload stream,
            // including when its actual value is zero.
            if (written != reader.Entry.Size || crc.GetCurrentHashAsUInt32() != expectedCrc)
                throw new InvalidDataException("Corrupt 7z entry size or CRC.");
            return written;
        }
        catch
        {
            // EntryStream.Dispose otherwise drains the remaining solid block, decoding after a
            // quota/cancellation failure and possibly replacing the original exception.
            reader.Cancel();
            throw;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private sealed class OpenedArchive : IDisposable
    {
        private readonly IArchive _archive;
        public IEnumerable<IArchiveEntry> Entries => _archive.Entries;
        public Dictionary<IEntry, uint> Crcs { get; } = [];
        public OpenedArchive(IArchive archive, IReadOnlyList<uint> crcs)
        {
            _archive = archive;
            int index = 0;
            foreach (var entry in archive.Entries)
                Crcs.Add(entry, crcs[index++]);
        }
        public IReader ExtractAllEntries() => _archive.ExtractAllEntries();
        public void Dispose() => _archive.Dispose();
    }

    private static T Decode<T>(Func<T> operation)
    {
        try
        { return operation(); }
        catch (Exception ex) when (ex is SharpCompressException or EndOfStreamException or NotSupportedException
            or ArgumentException or IndexOutOfRangeException or OverflowException or InvalidOperationException or NullReferenceException)
        { throw new InvalidDataException("Invalid or unsupported 7z archive: " + ex.Message, ex); }
    }

    private static string NormalizePath(string path)
    {
        if (path.Length > JobItem.MaxFileNameLength)
            throw new InvalidDataException("An archive entry exceeds the upload relative-path limit.");
        path = path.Replace('\\', '/');
        if (path.StartsWith('/') || path.Contains(':') || path.Contains('\0'))
            throw new InvalidDataException("Absolute paths, drive letters and NUL are forbidden in 7z entries.");
        var parts = new List<string>();
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (parts.Count == 0)
                    throw new InvalidDataException("A 7z entry escapes the extraction directory.");
                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                // Windows trims trailing dots/spaces and recognizes these names as devices.
                // Reject the aliases so the tree is deterministic across supported filesystems.
                string stem = part.Split('.')[0];
                if (part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => c < ' ') || part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0
                    || stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                    || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                    || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                       && stem[3] is >= '1' and <= '9')
                    throw new InvalidDataException("A 7z entry uses a non-portable path.");
                parts.Add(part);
            }
        }
        if (parts.Count == 0)
            throw new InvalidDataException("Empty 7z entry path.");
        return string.Join('/', parts);
    }

    private static FileStream OpenNew(string path, string name)
    {
        try
        { return File.Open(path, FileMode.CreateNew, FileAccess.Write); }
        catch (IOException) when (Path.Exists(path)) { throw new ArchiveEntryCollisionException(name); }
    }
    private static void ChargeEntry(IExtractionQuota quota)
    {
        if (!quota.TryChargeEntry())
            throw new ExtractionQuotaExceededException("Creating another file or directory exceeds the extraction entry budget.");
    }

    private sealed class OutputPaths(string target, IExtractionQuota quota)
    {
        private readonly string _root = Path.GetFullPath(target);
        private readonly HashSet<string> _explicit = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Spelling, bool Directory)> _nodes = new(StringComparer.OrdinalIgnoreCase);
        public void CreateRoot() => CreateCharged(_root, "");
        public string Claim(string relative, bool directory, string name)
        {
            if (!_explicit.Add(relative))
                throw new ArchiveEntryCollisionException(name);
            Register(relative, directory, name);
            return Path.Combine(_root, relative);
        }
        private void Register(string relative, bool directory, string name)
        {
            if (_nodes.TryGetValue(relative, out var prior) && (prior.Spelling != relative || prior.Directory != directory))
                throw new ArchiveEntryCollisionException(name);
            _nodes[relative] = (relative, directory);
        }
        public void CreateDirectory(string relative, string name)
        {
            string current = "";
            foreach (string part in relative.Split('/'))
            {
                current = current.Length == 0 ? part : current + "/" + part;
                Register(current, true, name);
                CreateCharged(Path.Combine(_root, current), name);
            }
        }
        private static void EnsureNoLink(string path)
        {
            if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Extraction cannot traverse symbolic links.");
        }
        private void CreateCharged(string path, string name)
        {
            var missing = new Stack<string>();
            for (string? current = Path.TrimEndingDirectorySeparator(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                EnsureNoLink(current);
                if (Directory.Exists(current))
                    break;
                if (File.Exists(current))
                    throw new ArchiveEntryCollisionException(name);
                missing.Push(current);
            }
            while (missing.TryPop(out string? directory))
            {
                ChargeEntry(quota);
                try
                { Directory.CreateDirectory(directory); }
                catch (IOException) when (File.Exists(directory)) { throw new ArchiveEntryCollisionException(name); }
            }
        }
    }
}
