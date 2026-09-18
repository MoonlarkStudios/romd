using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Storage.Files;

/// <summary>
///     Represents a handle to a temporary file used for processing.
///     The file is automatically deleted when this object is disposed.
/// </summary>
public interface ITempFile : IAsyncDisposable
{
    /// <summary>
    ///     Gets the physical path to the temp file (if available).
    ///     Useful for passing to external CLI tools.
    /// </summary>
    string Path { get; }

    /// <summary>
    ///     Gets the SHA256 hash of the file content, computed during creation.
    /// </summary>
    Sha256 FileSha256 { get; }

    /// <summary>
    ///     Gets the size of the file in bytes.
    /// </summary>
    long FileSize { get; }

    /// <summary>
    ///     Opens a new readable, seekable stream to the temp file.
    ///     Caller is responsible for disposing the stream.
    /// </summary>
    Stream OpenRead();
}
