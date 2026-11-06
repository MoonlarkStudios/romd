using ErrorOr;
using Romd.Dat.Parsing.Models;

namespace Romd.Dat.Parsing;

public interface IDatGameStreamReader
{
    IAsyncEnumerable<ErrorOr<ParsedGame>> StreamGamesAsync(
        Stream stream,
        CancellationToken ct = default);
}
