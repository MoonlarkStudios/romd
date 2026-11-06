using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Dat.Parsing.Diagnostics;
using Romd.Dat.Parsing.Models;

namespace Romd.Dat.Parsing.Formats.Logiqx;

/// <summary>
///     Parses DAT files in Logiqx XML format (used by No-Intro, Redump, MAME, and others).
///     Implements streaming parsing for memory-efficient processing of large DAT files.
/// </summary>
public sealed class LogiqxDatFormat : IDatFormat
{
    private readonly ILogger<LogiqxDatFormat> _logger;

    public LogiqxDatFormat(ILogger<LogiqxDatFormat> logger)
    {
        _logger = logger;
    }

    #region Public Interface

    /// <inheritdoc />
    public bool CanRead(ReadOnlySpan<byte> prefix) =>
        ContainsLogiqxSignature(DecodeWithoutBom(prefix).TrimStart());

    public async Task<ErrorOr<ParsedHeader>> ParseHeaderAsync(
        Stream stream,
        CancellationToken ct)
    {
        try
        {
            using var reader = CreateXmlReader(stream);

            while (await reader.ReadAsync())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.LocalName.Equals("header", StringComparison.OrdinalIgnoreCase))
                {
                    var headerResult = await ParseHeaderElementAsync(reader, ct);
                    if (headerResult.IsError)
                    {
                        return headerResult.Errors;
                    }

                    return headerResult.Value;
                }

                if (IsGameElement(reader.LocalName))
                {
                    return DatParseErrors.MissingRequiredElement("header");
                }
            }

            return DatParseErrors.MissingRequiredElement("header");
        }
        catch (XmlException ex)
        {
            return DatParseErrors.ParseFailed(ex.Message);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ErrorOr<ParsedGame>> ParseGamesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = CreateXmlReader(stream);

        while (await reader.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName.Equals("header", StringComparison.OrdinalIgnoreCase))
            {
                await SkipCurrentElementAsync(reader);
                continue;
            }

            if (IsGameElement(reader.LocalName))
            {
                var parsedGame = await ParseGameElementAsync(reader, ct);

                if (parsedGame is not null)
                {
                    yield return parsedGame;
                }
            }
        }
    }

    #endregion

    #region Header Parsing

    private static async Task<ErrorOr<ParsedHeader>> ParseHeaderElementAsync(
        XmlReader reader,
        CancellationToken ct)
    {
        if (reader.IsEmptyElement)
        {
            return DatParseErrors.MissingRequiredElement("header/name");
        }

        int elementDepth = reader.Depth;
        var header = new HeaderBuilder();

        // Track whether ReadElementContentAsStringAsync was called, which advances the reader
        // past the end tag. Without this flag, the next ReadAsync() would skip an element.
        bool contentWasRead = false;

        while (contentWasRead || await reader.ReadAsync())
        {
            contentWasRead = false;
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == elementDepth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            string elementName = reader.LocalName.ToLowerInvariant();
            string content = await reader.ReadElementContentAsStringAsync();
            contentWasRead = true;

            switch (elementName)
            {
                case "name":
                    header.Name = content;
                    break;
                case "description":
                    header.Description = content;
                    break;
                case "version":
                    header.Version = content;
                    break;
                case "author":
                    header.Author = content;
                    break;
                case "homepage":
                    header.Homepage = content;
                    break;
                case "url":
                    header.Url = content;
                    break;
                case "date":
                    header.Date = content;
                    break;
                case "email":
                    header.Email = content;
                    break;
                case "comment":
                    header.Comment = content;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(header.Name))
        {
            return DatParseErrors.MissingRequiredElement("header/name");
        }

        return header.Build();
    }

    /// <summary>
    ///     Mutable builder for header fields during parsing.
    /// </summary>
    private sealed class HeaderBuilder
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Version { get; set; }
        public string? Author { get; set; }
        public string? Homepage { get; set; }
        public string? Url { get; set; }
        public string? Date { get; set; }
        public string? Email { get; set; }
        public string? Comment { get; set; }

        public ParsedHeader Build() => new()
        {
            Name = Name!,
            Description = Description ?? Name!,
            Version = Version,
            Author = Author,
            Homepage = Homepage,
            Url = Url ?? Homepage,
            Date = Date,
            Email = Email,
            Comment = Comment
        };
    }

    #endregion

    #region Game Parsing

    private async Task<ParsedGame?> ParseGameElementAsync(XmlReader reader, CancellationToken ct)
    {
        // Game/machine name is required and comes from an attribute
        string? name = reader.GetAttribute("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            _logger.LogWarning("Skipping game element with missing name attribute");
            await SkipCurrentElementAsync(reader);
            return null;
        }

        // Optional attributes
        string? cloneOf = reader.GetAttribute("cloneof");
        string? romOf = reader.GetAttribute("romof");
        string? isBiosAttr = reader.GetAttribute("isbios");

        // Empty element has no children to parse
        if (reader.IsEmptyElement)
        {
            bool isBios = BiosDetection.IsBiosGame(isBiosAttr, null, name);
            return new ParsedGame
            {
                Name = name,
                CloneOf = cloneOf,
                RomOf = romOf,
                IsBios = isBios,
                NameMetadata = DatNameParser.Parse(name),
                Roms = [],
                Disks = []
            };
        }

        int elementDepth = reader.Depth;
        string? description = null;
        string? year = null;
        string? manufacturer = null;
        string? category = null;
        var roms = new List<ParsedRom>();
        var disks = new List<ParsedDisk>();

        bool contentWasRead = false;

        while (contentWasRead || await reader.ReadAsync())
        {
            contentWasRead = false;
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == elementDepth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName.ToLowerInvariant())
            {
                case "description":
                    description = await reader.ReadElementContentAsStringAsync();
                    contentWasRead = true;
                    break;

                case "year":
                    year = await reader.ReadElementContentAsStringAsync();
                    contentWasRead = true;
                    break;

                case "manufacturer":
                    manufacturer = await reader.ReadElementContentAsStringAsync();
                    contentWasRead = true;
                    break;

                case "category":
                    category = await reader.ReadElementContentAsStringAsync();
                    contentWasRead = true;
                    break;

                case "rom":
                    if (TryParseRomElement(reader, name, out var rom))
                    {
                        roms.Add(rom);
                    }

                    await SkipCurrentElementAsync(reader);
                    break;

                case "disk":
                    if (TryParseDiskElement(reader, out var disk))
                    {
                        disks.Add(disk);
                    }

                    await SkipCurrentElementAsync(reader);
                    break;

                default:
                    await SkipCurrentElementAsync(reader);
                    break;
            }
        }

        return new ParsedGame
        {
            Name = name,
            Description = description,
            Year = year,
            Manufacturer = manufacturer,
            CloneOf = cloneOf,
            RomOf = romOf,
            NameMetadata = DatNameParser.Parse(name, category),
            IsBios = BiosDetection.IsBiosGame(isBiosAttr, category, name),
            Roms = roms,
            Disks = disks
        };
    }

    private bool TryParseRomElement(XmlReader reader, string gameName, out ParsedRom rom)
    {
        string? name = reader.GetAttribute("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            _logger.LogWarning("Skipping ROM in game '{GameName}': missing name attribute", gameName);
            rom = default!;
            return false;
        }

        // NumberStyles.None rejects signed sizes; corrupt declarations fall back to 0.
        _ = long.TryParse(
            reader.GetAttribute("size"), NumberStyles.None, CultureInfo.InvariantCulture, out long size);

        rom = new ParsedRom
        {
            Name = name,
            Size = size,
            Crc = HashNormalizer.Crc32(reader.GetAttribute("crc")),
            Md5 = HashNormalizer.Md5(reader.GetAttribute("md5")),
            Sha1 = HashNormalizer.Sha1(reader.GetAttribute("sha1")),
            Status = reader.GetAttribute("status"),
            Serial = reader.GetAttribute("serial")
        };

        return true;
    }

    private bool TryParseDiskElement(XmlReader reader, out ParsedDisk disk)
    {
        string? name = reader.GetAttribute("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            disk = default!;
            return false;
        }

        disk = new ParsedDisk
        {
            Name = name,
            Sha1 = HashNormalizer.Sha1(reader.GetAttribute("sha1")),
            Md5 = HashNormalizer.Md5(reader.GetAttribute("md5")),
            Status = reader.GetAttribute("status")
        };

        return true;
    }

    #endregion

    #region XML Reader Utilities

    private static XmlReader CreateXmlReader(Stream stream) =>
        XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null, // Security: prevents XXE attacks
            CloseInput = false // Critical: stream must remain open for subsequent parsing passes
        });

    /// <summary>
    ///     Advances the reader past the current element and all its descendants.
    ///     Safe to call on empty elements.
    /// </summary>
    private static async Task SkipCurrentElementAsync(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        int targetDepth = reader.Depth;

        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == targetDepth)
            {
                return;
            }
        }
    }

    #endregion

    #region Format Detection

    private static string DecodeWithoutBom(ReadOnlySpan<byte> prefix)
    {
        // Skip UTF-8 BOM if present
        if (prefix is [0xEF, 0xBB, 0xBF, ..])
        {
            return Encoding.UTF8.GetString(prefix[3..]);
        }

        return Encoding.UTF8.GetString(prefix);
    }

    private static bool ContainsLogiqxSignature(string content) =>
        content.Contains("<datafile", StringComparison.OrdinalIgnoreCase) ||
        content.Contains("<dat", StringComparison.OrdinalIgnoreCase) ||
        content.Contains("<!DOCTYPE datafile", StringComparison.OrdinalIgnoreCase);

    private static bool IsGameElement(string localName) =>
        localName.Equals("game", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("machine", StringComparison.OrdinalIgnoreCase);

    #endregion

}
