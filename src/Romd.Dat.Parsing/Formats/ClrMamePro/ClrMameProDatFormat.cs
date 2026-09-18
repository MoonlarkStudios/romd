using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Dat.Parsing.Diagnostics;
using Romd.Dat.Parsing.Models;

namespace Romd.Dat.Parsing.Formats.ClrMamePro;

/// <summary>
///     Parses DAT files in clrmamepro (CMP) text format — the parenthesized format Redump and
///     others distribute (including dedicated "BIOS Images" sets). Streams tokens so large DATs
///     are processed without buffering the whole file.
/// </summary>
public sealed class ClrMameProDatFormat : IDatFormat
{
    private const int ReadBufferSize = 8192;

    private readonly ILogger<ClrMameProDatFormat> _logger;

    public ClrMameProDatFormat(ILogger<ClrMameProDatFormat> logger)
    {
        _logger = logger;
    }

    #region Public Interface

    /// <inheritdoc />
    public bool CanRead(ReadOnlySpan<byte> prefix) =>
        DecodeWithoutBom(prefix).TrimStart()
            .StartsWith("clrmamepro", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<ErrorOr<ParsedHeader>> ParseHeaderAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, ReadBufferSize, leaveOpen: true);
        var tokenizer = new Tokenizer(reader);

        while (true)
        {
            string? ident = await ReadBlockStartAsync(tokenizer, ct);
            if (ident is null)
            {
                return DatParseErrors.MissingRequiredElement("header");
            }

            if (IsHeaderIdent(ident))
            {
                var header = await ParseHeaderBodyAsync(tokenizer, ct);
                if (string.IsNullOrWhiteSpace(header.Name))
                {
                    return DatParseErrors.MissingRequiredElement("header/name");
                }

                return header;
            }

            if (IsGameIdent(ident))
            {
                return DatParseErrors.MissingRequiredElement("header");
            }

            await SkipBlockBodyAsync(tokenizer, ct);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ErrorOr<ParsedGame>> ParseGamesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, ReadBufferSize, leaveOpen: true);
        var tokenizer = new Tokenizer(reader);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            string? ident = await ReadBlockStartAsync(tokenizer, ct);
            if (ident is null)
            {
                yield break;
            }

            if (IsHeaderIdent(ident))
            {
                await SkipBlockBodyAsync(tokenizer, ct);
            }
            else if (IsGameIdent(ident))
            {
                var parsed = await ParseGameBodyAsync(tokenizer, ct);
                if (parsed is not null)
                {
                    yield return parsed;
                }
            }
            else
            {
                await SkipBlockBodyAsync(tokenizer, ct);
            }
        }
    }

    #endregion

    #region Block Parsing

    /// <summary>
    ///     Reads the next <c>identifier (</c> sequence, consuming both the identifier and the open
    ///     paren. Returns the lowercased identifier, or null at end of input.
    /// </summary>
    private static async ValueTask<string?> ReadBlockStartAsync(Tokenizer tokenizer, CancellationToken ct)
    {
        while (true)
        {
            var token = await tokenizer.NextAsync(ct);
            if (token.Type == TokenType.End)
            {
                return null;
            }

            if (token.Type != TokenType.Symbol)
            {
                continue;
            }

            var open = await tokenizer.NextAsync(ct);
            if (open.Type == TokenType.Open)
            {
                return token.Value.ToLowerInvariant();
            }

            if (open.Type == TokenType.End)
            {
                return null;
            }
        }
    }

    private static async ValueTask<ParsedHeader> ParseHeaderBodyAsync(Tokenizer tokenizer, CancellationToken ct)
    {
        string? name = null;
        string? description = null;
        string? version = null;
        string? author = null;
        string? homepage = null;
        string? url = null;
        string? date = null;
        string? email = null;
        string? comment = null;

        await foreach (var (key, value) in ReadFieldsAsync(tokenizer, ct))
        {
            switch (key)
            {
                case "name": name = value; break;
                case "description": description = value; break;
                case "version": version = value; break;
                case "author": author = value; break;
                case "homepage": homepage = value; break;
                case "url": url = value; break;
                case "date": date = value; break;
                case "email": email = value; break;
                case "comment": comment = value; break;
            }
        }

        name ??= string.Empty;
        return new ParsedHeader
        {
            Name = name,
            Description = description ?? name,
            Version = version,
            Author = author,
            Homepage = homepage,
            Url = url ?? homepage,
            Date = date,
            Email = email,
            Comment = comment
        };
    }

    private async ValueTask<ParsedGame?> ParseGameBodyAsync(Tokenizer tokenizer, CancellationToken ct)
    {
        string? name = null;
        string? description = null;
        string? year = null;
        string? manufacturer = null;
        string? cloneOf = null;
        string? romOf = null;
        string? category = null;
        var roms = new List<ParsedRom>();
        var disks = new List<ParsedDisk>();

        while (true)
        {
            var token = await tokenizer.NextAsync(ct);
            if (token.Type is TokenType.Close or TokenType.End)
            {
                break;
            }

            if (token.Type != TokenType.Symbol)
            {
                continue;
            }

            string key = token.Value.ToLowerInvariant();

            var peek = await tokenizer.PeekAsync(ct);
            if (peek.Type == TokenType.Open)
            {
                await tokenizer.NextAsync(ct); // consume '('
                switch (key)
                {
                    case "rom":
                        var rom = await ParseRomBodyAsync(tokenizer, ct);
                        if (rom is not null) roms.Add(rom);
                        break;
                    case "disk":
                        var disk = await ParseDiskBodyAsync(tokenizer, ct);
                        if (disk is not null) disks.Add(disk);
                        break;
                    default:
                        await SkipBlockBodyAsync(tokenizer, ct);
                        break;
                }

                continue;
            }

            var value = await tokenizer.NextAsync(ct);
            if (value.Type is TokenType.Close or TokenType.End)
            {
                break;
            }

            if (value.Type != TokenType.Symbol)
            {
                continue;
            }

            switch (key)
            {
                case "name": name = value.Value; break;
                case "description": description = value.Value; break;
                case "year": year = value.Value; break;
                case "manufacturer": manufacturer = value.Value; break;
                case "cloneof": cloneOf = value.Value; break;
                case "romof": romOf = value.Value; break;
                case "category": category = value.Value; break;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            _logger.LogWarning("Skipping clrmamepro game block with missing name");
            return null;
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
            IsBios = BiosDetection.IsBiosGame(null, category, name),
            Roms = roms,
            Disks = disks
        };
    }

    private static async ValueTask<ParsedRom?> ParseRomBodyAsync(Tokenizer tokenizer, CancellationToken ct)
    {
        string? name = null;
        string? crc = null;
        string? md5 = null;
        string? sha1 = null;
        string? status = null;
        string? serial = null;
        long size = 0;

        await foreach (var (key, value) in ReadFieldsAsync(tokenizer, ct))
        {
            switch (key)
            {
                case "name": name = value; break;
                // NumberStyles.None rejects signed sizes; corrupt declarations fall back to 0.
                case "size": _ = long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out size); break;
                case "crc": crc = value; break;
                case "md5": md5 = value; break;
                case "sha1": sha1 = value; break;
                case "status":
                case "flags": status = value; break;
                case "serial": serial = value; break;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new ParsedRom
        {
            Name = name,
            Size = size,
            Crc = HashNormalizer.Crc32(crc),
            Md5 = HashNormalizer.Md5(md5),
            Sha1 = HashNormalizer.Sha1(sha1),
            Status = status,
            Serial = serial
        };
    }

    private static async ValueTask<ParsedDisk?> ParseDiskBodyAsync(Tokenizer tokenizer, CancellationToken ct)
    {
        string? name = null;
        string? md5 = null;
        string? sha1 = null;
        string? status = null;

        await foreach (var (key, value) in ReadFieldsAsync(tokenizer, ct))
        {
            switch (key)
            {
                case "name": name = value; break;
                case "md5": md5 = value; break;
                case "sha1": sha1 = value; break;
                case "status":
                case "flags": status = value; break;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new ParsedDisk
        {
            Name = name,
            Sha1 = HashNormalizer.Sha1(sha1),
            Md5 = HashNormalizer.Md5(md5),
            Status = status
        };
    }

    /// <summary>
    ///     Yields <c>key value</c> pairs from a block body until its closing paren. Nested blocks
    ///     (which leaf blocks like rom/disk never contain) are skipped.
    /// </summary>
    private static async IAsyncEnumerable<(string Key, string Value)> ReadFieldsAsync(
        Tokenizer tokenizer,
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            var token = await tokenizer.NextAsync(ct);
            if (token.Type is TokenType.Close or TokenType.End)
            {
                yield break;
            }

            if (token.Type != TokenType.Symbol)
            {
                continue;
            }

            string key = token.Value.ToLowerInvariant();

            var peek = await tokenizer.PeekAsync(ct);
            if (peek.Type == TokenType.Open)
            {
                await tokenizer.NextAsync(ct); // consume '('
                await SkipBlockBodyAsync(tokenizer, ct);
                continue;
            }

            var value = await tokenizer.NextAsync(ct);
            if (value.Type is TokenType.Close or TokenType.End)
            {
                yield break;
            }

            if (value.Type != TokenType.Symbol)
            {
                continue;
            }

            yield return (key, value.Value);
        }
    }

    private static async ValueTask SkipBlockBodyAsync(Tokenizer tokenizer, CancellationToken ct)
    {
        int depth = 1;
        while (depth > 0)
        {
            var token = await tokenizer.NextAsync(ct);
            if (token.Type == TokenType.End)
            {
                return;
            }

            if (token.Type == TokenType.Open)
            {
                depth++;
            }
            else if (token.Type == TokenType.Close)
            {
                depth--;
            }
        }
    }

    #endregion

    #region Helpers

    private static bool IsHeaderIdent(string ident) =>
        ident is "clrmamepro" or "emulator";

    private static bool IsGameIdent(string ident) =>
        ident is "game" or "machine" or "set";

    private static string DecodeWithoutBom(ReadOnlySpan<byte> prefix)
    {
        if (prefix is [0xEF, 0xBB, 0xBF, ..])
        {
            return Encoding.UTF8.GetString(prefix[3..]);
        }

        return Encoding.UTF8.GetString(prefix);
    }

    #endregion

    #region Tokenizer

    private enum TokenType
    {
        Open,
        Close,
        Symbol,
        End
    }

    private readonly record struct Token(TokenType Type, string Value)
    {
        public static readonly Token Open = new(TokenType.Open, "(");
        public static readonly Token Close = new(TokenType.Close, ")");
        public static readonly Token End = new(TokenType.End, "");
        public static Token Symbol(string value) => new(TokenType.Symbol, value);
    }

    /// <summary>
    ///     Streaming clrmamepro tokenizer with single-token lookahead. Tokens are parentheses,
    ///     quoted strings (double quotes, no escaping per the CMP convention), and bare words.
    /// </summary>
    private sealed class Tokenizer
    {
        private readonly char[] _buffer = new char[ReadBufferSize];
        private readonly TextReader _reader;
        private int _length;
        private int _position;
        private Token? _peeked;

        public Tokenizer(TextReader reader) => _reader = reader;

        public async ValueTask<Token> PeekAsync(CancellationToken ct)
        {
            _peeked ??= await ReadTokenAsync(ct);
            return _peeked.Value;
        }

        public async ValueTask<Token> NextAsync(CancellationToken ct)
        {
            if (_peeked is { } peeked)
            {
                _peeked = null;
                return peeked;
            }

            return await ReadTokenAsync(ct);
        }

        private async ValueTask<int> PeekCharAsync(CancellationToken ct)
        {
            if (_position >= _length)
            {
                _length = await _reader.ReadAsync(_buffer.AsMemory(), ct);
                _position = 0;
                if (_length == 0)
                {
                    return -1;
                }
            }

            return _buffer[_position];
        }

        private async ValueTask<Token> ReadTokenAsync(CancellationToken ct)
        {
            int c;
            while ((c = await PeekCharAsync(ct)) != -1 && char.IsWhiteSpace((char)c))
            {
                _position++;
            }

            if (c == -1)
            {
                return Token.End;
            }

            switch (c)
            {
                case '(':
                    _position++;
                    return Token.Open;
                case ')':
                    _position++;
                    return Token.Close;
                case '"':
                    _position++;
                    return await ReadQuotedAsync(ct);
                default:
                    return await ReadBareAsync(ct);
            }
        }

        private async ValueTask<Token> ReadQuotedAsync(CancellationToken ct)
        {
            var sb = new StringBuilder();
            int c;
            while ((c = await PeekCharAsync(ct)) != -1)
            {
                _position++;
                if (c == '"')
                {
                    break;
                }

                sb.Append((char)c);
            }

            return Token.Symbol(sb.ToString());
        }

        private async ValueTask<Token> ReadBareAsync(CancellationToken ct)
        {
            var sb = new StringBuilder();
            int c;
            while ((c = await PeekCharAsync(ct)) != -1
                   && !char.IsWhiteSpace((char)c)
                   && c != '('
                   && c != ')'
                   && c != '"')
            {
                _position++;
                sb.Append((char)c);
            }

            return Token.Symbol(sb.ToString());
        }
    }

    #endregion
}
