using ErrorOr;
using Romd.Dat.Models;

namespace Romd.Dat;

public interface IDatHeaderReader
{
    Task<ErrorOr<ParsedHeader>> ReadHeaderAsync(
        Stream stream,
        CancellationToken ct = default);
}
