using ErrorOr;
using Romd.Dat.Parsing.Models;

namespace Romd.Dat.Parsing;

/// <summary>
///     Extension point for a cohesive DAT syntax implementation.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="DatParser" /> owns the input stream. It supplies a bounded prefix to
///         <see cref="CanRead" /> and positions seekable streams at offset zero before delegating
///         parsing operations.
///     </para>
///     <para>
///         Implementations must parse from the supplied current position and leave the stream open.
///         Format implementations own syntax and per-game interpretation only; parser-level source
///         assertions are composed by <see cref="DatParser" />.
///     </para>
/// </remarks>
public interface IDatFormat
{
    bool CanRead(ReadOnlySpan<byte> prefix);

    Task<ErrorOr<ParsedHeader>> ParseHeaderAsync(Stream stream, CancellationToken ct);

    IAsyncEnumerable<ErrorOr<ParsedGame>> ParseGamesAsync(
        Stream stream,
        CancellationToken ct);
}
