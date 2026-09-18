using ErrorOr;
using Romd.Dat.Parsing.Models;

namespace Romd.Dat.Parsing;

public interface IDatHeaderReader
{
    Task<ErrorOr<ParsedHeader>> ReadHeaderAsync(
        Stream stream,
        CancellationToken ct = default);
}
