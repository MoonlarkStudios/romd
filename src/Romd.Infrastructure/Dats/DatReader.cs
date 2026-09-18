using System.Runtime.CompilerServices;
using ErrorOr;
using Romd.Admin.Application.Source.Dat;
using Romd.Dat.Parsing;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Dats.Format;

namespace Romd.Infrastructure.Dats;

public sealed class DatReader(
    IDatHeaderReader headerReader,
    IDatGameStreamReader gameStreamReader) : IDatReader
{
    public async Task<ErrorOr<DatMetadata>> ReadHeaderAsync(
        Stream stream,
        CancellationToken ct = default)
    {
        var result = await headerReader.ReadHeaderAsync(stream, ct);
        if (result.IsError)
        {
            return result.Errors;
        }

        return DatModelMapper.ToMetadata(result.Value);
    }

    public async IAsyncEnumerable<ErrorOr<DatGame>> StreamGamesAsync(
        Stream stream,
        int datFileId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var result in gameStreamReader.StreamGamesAsync(stream, ct))
        {
            yield return result.IsError
                ? result.Errors
                : DatModelMapper.ToDatGame(result.Value, datFileId);
        }
    }
}
