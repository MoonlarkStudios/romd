using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Storage.Files;

/// <summary>
///     Represents a handle to a temporary file used for processing.
///     Disposal deletes the file unless an active child or quarantine retains custody.
/// </summary>
public interface ITempFile : IAsyncDisposable
{
    /// <summary>
    ///     Gets the physical path to the temp file (if available).
    ///     Useful for passing to external CLI tools.
    /// </summary>
    string Path { get; }

    /// <summary>DAT hashes measured during staging, when supported by the factory.</summary>
    Romd.Admin.Application.Hashing.DatHashResult? DatHashes => null;

    /// <summary>
    ///     Gets the SHA256 hash of the file content, computed during creation.
    /// </summary>
    Sha256 FileSha256 { get; }

    /// <summary>
    ///     Gets the size of the file in bytes.
    /// </summary>
    long FileSize { get; }

    /// <summary>Persist source custody before launching a child; false prevents launch.</summary>
    bool TryBeginChildUse();

    /// <summary>Release child custody only after its process tree and readers have stopped.</summary>
    bool TryEndChildUse();

    /// <summary>Retain custody permanently; true also confirms a durable quarantine marker.</summary>
    bool Quarantine();

    /// <summary>
    ///     Opens a new readable, seekable stream to the temp file.
    ///     Caller is responsible for disposing the stream.
    /// </summary>
    Stream OpenRead();
}
