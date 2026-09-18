using System.Runtime.CompilerServices;
using ErrorOr;
using Romd.Dat.Parsing.Diagnostics;
using Romd.Dat.Parsing.Models;

namespace Romd.Dat.Parsing;

public sealed class DatParser(IEnumerable<IDatFormat> formats) : IDatHeaderReader, IDatGameStreamReader
{
    private const int DetectionPrefixLength = 1024;
    private readonly IReadOnlyList<IDatFormat> _formats = formats.ToList();

    public async Task<ErrorOr<ParsedHeader>> ReadHeaderAsync(
        Stream stream,
        CancellationToken ct = default)
    {
        var formatResult = await DetectFormatAsync(stream, ct);
        if (formatResult.IsError)
        {
            return formatResult.Errors;
        }

        stream.Position = 0;
        var result = await formatResult.Value.ParseHeaderAsync(stream, ct);
        return result.IsError ? result.Errors : StampProvenance(result.Value);
    }

    public async IAsyncEnumerable<ErrorOr<ParsedGame>> StreamGamesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var formatResult = await DetectFormatAsync(stream, ct);
        if (formatResult.IsError)
        {
            yield return formatResult.Errors;
            yield break;
        }

        stream.Position = 0;
        var headerResult = await formatResult.Value.ParseHeaderAsync(stream, ct);
        bool isBiosDat = !headerResult.IsError && BiosDetection.IsBiosDat(headerResult.Value.Name);

        stream.Position = 0;
        await foreach (var game in formatResult.Value.ParseGamesAsync(stream, ct))
        {
            yield return game.IsError
                ? game.Errors
                : game.Value with { IsBios = game.Value.IsBios || isBiosDat };
        }
    }

    private async Task<ErrorOr<IDatFormat>> DetectFormatAsync(Stream stream, CancellationToken ct)
    {
        if (!stream.CanSeek)
        {
            return DatParseErrors.StreamNotSeekable;
        }

        stream.Position = 0;
        byte[] prefix = new byte[DetectionPrefixLength];
        int bytesRead = await stream.ReadAsync(prefix.AsMemory(), ct);

        foreach (var format in _formats)
        {
            if (format.CanRead(prefix.AsSpan(0, bytesRead)))
            {
                return format.ToErrorOr();
            }
        }

        return DatParseErrors.UnknownFormat;
    }

    private static ParsedHeader StampProvenance(ParsedHeader header) => header with
    {
        Provenance = DatProvenanceDetector.Detect(
            header.Name,
            header.Author,
            header.Url ?? header.Homepage)
    };
}
