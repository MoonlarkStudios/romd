using System.Globalization;
using System.Text;
using System.Xml;
using Romd.Dat.Models;

namespace Romd.Dat.Formats.Logiqx;

/// <summary>
///     Writes the modeled DAT declarations as Logiqx XML in the caller's entry order.
/// </summary>
public static class LogiqxDatWriter
{
    /// <summary>
    ///     Writes UTF-8 to a caller-owned stream and leaves it open. A failed or cancelled
    ///     write can leave partial output. Publish the destination only after success.
    ///     Cancellation is checked between entries, files, and after the final flush.
    /// </summary>
    public static async Task WriteAsync(
        Stream destination, ParsedHeader header, IAsyncEnumerable<ParsedGame> games, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(games);
        if (!destination.CanWrite)
            throw new ArgumentException("The DAT destination must be writable.", nameof(destination));
        ct.ThrowIfCancellationRequested();
        RequireName(header.Name, "header");

        await using var entries = games.GetAsyncEnumerator(ct);
        if (!await entries.MoveNextAsync())
            throw new InvalidDataException("A DAT must contain at least one entry.");
        ct.ThrowIfCancellationRequested();

        await using var xml = XmlWriter.Create(destination, new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Entitize,
            CloseOutput = false,
            WriteEndDocumentOnClose = false
        });

        await xml.WriteStartDocumentAsync();
        await xml.WriteStartElementAsync(null, "datafile", null);
        await WriteHeaderAsync(xml, header);

        var gameNames = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            ct.ThrowIfCancellationRequested();
            var game = entries.Current;
            RequireName(game.Name, "entry");
            if (!gameNames.Add(game.Name))
                throw new InvalidDataException($"Duplicate DAT entry '{game.Name}'.");
            await WriteGameAsync(xml, game, ct);
            ct.ThrowIfCancellationRequested();
        }
        while (await entries.MoveNextAsync());

        ct.ThrowIfCancellationRequested();
        await xml.WriteEndElementAsync();
        await xml.WriteEndDocumentAsync();
        await xml.FlushAsync();
        ct.ThrowIfCancellationRequested();
    }

    private static async Task WriteHeaderAsync(XmlWriter xml, ParsedHeader header)
    {
        await xml.WriteStartElementAsync(null, "header", null);
        await WriteElementAsync(xml, "name", header.Name);
        await WriteElementAsync(xml, "description", header.Description);
        await WriteElementAsync(xml, "version", header.Version);
        await WriteElementAsync(xml, "date", header.Date);
        await WriteElementAsync(xml, "author", header.Author);
        await WriteElementAsync(xml, "email", header.Email);
        await WriteElementAsync(xml, "homepage", header.Homepage);
        // A reader may have populated Url from Homepage. The same fallback will apply on re-read.
        if (!StringComparer.Ordinal.Equals(header.Url, header.Homepage))
            await WriteElementAsync(xml, "url", header.Url);
        await WriteElementAsync(xml, "comment", header.Comment);
        await xml.WriteEndElementAsync();
    }

    private static async Task WriteGameAsync(XmlWriter xml, ParsedGame game, CancellationToken ct)
    {
        await xml.WriteStartElementAsync(null, "game", null);
        await WriteAttributeAsync(xml, "name", game.Name);
        await WriteAttributeAsync(xml, "id", game.DeclaredId);
        await WriteAttributeAsync(xml, "cloneof", game.CloneOf);
        await WriteAttributeAsync(xml, "cloneofid", game.CloneOfId);
        await WriteAttributeAsync(xml, "romof", game.RomOf);
        await WriteAttributeAsync(xml, "isbios", game.BiosDeclaration);
        await WriteElementAsync(xml, "description", game.Description);
        await WriteElementAsync(xml, "year", game.Year);
        await WriteElementAsync(xml, "manufacturer", game.Manufacturer);
        await WriteElementAsync(xml, "category", game.DeclaredCategory);

        var fileNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rom in game.Roms)
        {
            ct.ThrowIfCancellationRequested();
            RequireUniqueFileName(fileNames, rom.Name, "ROM");
            await WriteRomAsync(xml, rom);
        }

        fileNames.Clear();
        foreach (var disk in game.Disks)
        {
            ct.ThrowIfCancellationRequested();
            RequireUniqueFileName(fileNames, disk.Name, "disk");
            await WriteDiskAsync(xml, disk);
        }

        await xml.WriteEndElementAsync();
    }

    private static async Task WriteRomAsync(XmlWriter xml, ParsedRom rom)
    {
        if (rom.HasMalformedSize || rom.DeclaredSize is < 0 || (rom.DeclaredSize is null && rom.Status != "nodump"))
            throw new InvalidDataException($"ROM '{rom.Name}' needs a valid byte size. A nodump record may omit it.");
        if (rom.HasMalformedChecksum)
            throw new InvalidDataException($"ROM '{rom.Name}' contains a malformed checksum declaration.");

        await xml.WriteStartElementAsync(null, "rom", null);
        await WriteAttributeAsync(xml, "name", rom.Name);
        await WriteAttributeAsync(xml, "size", rom.DeclaredSize?.ToString(CultureInfo.InvariantCulture));
        await WriteChecksumAsync(xml, "crc", rom.Crc, HashNormalizer.Crc32);
        await WriteChecksumAsync(xml, "md5", rom.Md5, HashNormalizer.Md5);
        await WriteChecksumAsync(xml, "sha1", rom.Sha1, HashNormalizer.Sha1);
        await WriteAttributeAsync(xml, "status", rom.Status);
        await WriteAttributeAsync(xml, "serial", rom.Serial);
        await xml.WriteEndElementAsync();
    }

    private static async Task WriteDiskAsync(XmlWriter xml, ParsedDisk disk)
    {
        if (disk.HasMalformedChecksum)
            throw new InvalidDataException($"Disk '{disk.Name}' contains a malformed checksum declaration.");

        await xml.WriteStartElementAsync(null, "disk", null);
        await WriteAttributeAsync(xml, "name", disk.Name);
        await WriteChecksumAsync(xml, "md5", disk.Md5, HashNormalizer.Md5);
        await WriteChecksumAsync(xml, "sha1", disk.Sha1, HashNormalizer.Sha1);
        await WriteAttributeAsync(xml, "status", disk.Status);
        await xml.WriteEndElementAsync();
    }

    private static Task WriteChecksumAsync(XmlWriter xml, string name, string? value, Func<string?, string?> normalize)
    {
        if (value is null)
            return Task.CompletedTask;
        string checksum = normalize(value) ?? throw new InvalidDataException($"Invalid DAT {name} checksum.");
        return WriteAttributeAsync(xml, name, checksum);
    }

    private static Task WriteAttributeAsync(XmlWriter xml, string name, string? value) =>
        value is null ? Task.CompletedTask : xml.WriteAttributeStringAsync(null, name, null, value);

    private static Task WriteElementAsync(XmlWriter xml, string name, string? value) =>
        value is null ? Task.CompletedTask : xml.WriteElementStringAsync(null, name, null, value);

    private static void RequireUniqueFileName(HashSet<string> names, string name, string kind)
    {
        RequireName(name, kind);
        if (!names.Add(name))
            throw new InvalidDataException($"Duplicate {kind} name '{name}' within a DAT entry.");
    }

    private static void RequireName(string name, string kind)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException($"A DAT {kind} must have a name.");
    }
}
