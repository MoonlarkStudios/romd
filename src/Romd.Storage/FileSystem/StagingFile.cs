namespace Romd.Storage.FileSystem;

/// <summary>
/// A uniquely named temporary file in the CAS staging directory, on the objects' volume so publication is an
/// atomic rename. Its only handle is opened with FileShare.None
/// (an exclusive flock on Unix, a sharing lock on Windows) and stays open from creation until the
/// file is published or discarded, so cleanup in any cooperating process can tell live staging files
/// from orphans. Publication must happen under the root mutation lease, which cleanup also holds.
/// </summary>
internal sealed class StagingFile : IAsyncDisposable
{
    internal const string Prefix = ".tmp-";
    internal const string DirectoryName = ".staging";

    private bool _published;

    private StagingFile(string path, FileStream stream)
    {
        Path = path;
        Stream = stream;
    }

    public string Path { get; }

    /// <summary>The single read/write handle; the owner rereads staged bytes through it.</summary>
    public FileStream Stream { get; }

    public static StagingFile Create(string directory, int bufferSize)
    {
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, $"{Prefix}{Guid.NewGuid():N}");
        return new StagingFile(path, new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, bufferSize, FileOptions.Asynchronous));
    }

    /// <summary>Recognizes only names emitted by this writer, never arbitrary similarly named files.</summary>
    public static bool IsStagingName(ReadOnlySpan<char> fileName) =>
        fileName.StartsWith(Prefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(fileName[Prefix.Length..], "N", out _);

    /// <summary>
    /// True when no live writer holds the file. False when it is locked or already gone. Callers must
    /// hold the root mutation lease so that the file cannot be published between the probe and deletion.
    /// </summary>
    public static bool IsAbandoned(string path)
    {
        try
        {
            File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.None).Dispose();
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException ex) when (MutationLease.IsLockContention(ex)) { return false; }
    }

    /// <summary>Forces the staged bytes to disk. Only a representation kept for publication pays for this.</summary>
    public void Sync()
    {
        Stream.Flush(flushToDisk: true);
        ContentStoreDiagnostics.SyncedBytes.Add(Stream.Length);
    }

    /// <summary>
    /// Closes the handle and renames the staged file without overwriting. Call under the root mutation
    /// lease: cleanup cannot observe the unlocked file between closing and renaming.
    /// </summary>
    public void Publish(string finalPath)
    {
        Stream.Dispose();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(finalPath)!);
        File.Move(Path, finalPath, false);
        _published = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (!_published)
                TryDelete(Path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: CleanupTemporaryFilesAsync removes an abandoned staging file later.
        }
    }
}
