using ErrorOr;

namespace Romd.Admin.Application.Storage.Files;

/// <summary>
///     Factory for creating local temporary files for processing.
/// </summary>
public interface ITempFileFactory
{
    /// <summary>Reserve the complete conservative output bound before a child can create its payload.</summary>
    Task<ErrorOr<IWritableTempFile>> ReserveOutputAsync(long maximumBytes, CancellationToken ct = default);

    /// <summary>Reserve a bounded generated CD descriptor with a .cue suffix required by chdman.</summary>
    Task<ErrorOr<IWritableTempFile>> ReserveCdCueAsync(long maximumBytes, CancellationToken ct = default);

    /// <summary>
    ///     Creates a temporary file from the provided stream.
    /// </summary>
    /// <param name="includeDatHashes">Optional decision using the bounded detection prefix. Null computes only the artifact SHA-256.</param>
    /// <param name="beforeCopy">Optional admission using that same prefix, before reserving staging space or copying the remaining bytes.</param>
    /// <param name="selectPrefixLength">Optional decision in at most two growth rounds: from four bytes to a total bounded prefix of at most 32,775 bytes. Short input stops growth. Consumed prefix bytes are replayed once during staging.</param>
    Task<ITempFile> CreateAsync(Stream source, CancellationToken cancellationToken = default,
        Func<ReadOnlyMemory<byte>, bool>? includeDatHashes = null,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task>? beforeCopy = null,
        Func<ReadOnlyMemory<byte>, int>? selectPrefixLength = null);
}
