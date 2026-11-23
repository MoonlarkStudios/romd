using ErrorOr;
using Romd.Dat.Models;

namespace Romd.Dat;

public interface IDatGameStreamReader
{
    IAsyncEnumerable<ErrorOr<ParsedGame>> StreamGamesAsync(
        Stream stream,
        CancellationToken ct = default);
}
