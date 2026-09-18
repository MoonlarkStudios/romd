using System.IO.Abstractions;
using System.Security.Cryptography;
using Romd.Application.Common.Configuration;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;

namespace Romd.Infrastructure.Storage;

public sealed class LocalTempFileFactory : ITempFileFactory
{
    private readonly IFileSystem _fileSystem;
    private readonly string _tempDirectory;

    public LocalTempFileFactory(IRomdOptions options, IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
        _tempDirectory = Path.Combine(options.DataDirectory, "temp");

        _fileSystem.Directory.CreateDirectory(_tempDirectory);
    }

    public async Task<ITempFile> CreateAsync(Stream source, CancellationToken cancellationToken = default)
    {
        string fileName = $"{Guid.NewGuid():N}.tmp";
        string filePath = Path.Combine(_tempDirectory, fileName);

        Sha256 fileSha256;
        long fileSize;

        // Compute hash while writing to temp file (single pass)
        using var sha256 = SHA256.Create();
        await using (var fileStream = _fileSystem.File.Create(filePath))
        await using (var hashStream = new CryptoStream(fileStream, sha256, CryptoStreamMode.Write))
        {
            await source.CopyToAsync(hashStream, cancellationToken);
        }

        fileSha256 = Sha256.Parse(Convert.ToHexStringLower(sha256.Hash!));
        fileSize = _fileSystem.FileInfo.New(filePath).Length;

        return new LocalTempFile(filePath, fileSha256, fileSize, _fileSystem);
    }

    private sealed class LocalTempFile : ITempFile
    {
        private readonly IFileSystem _fs;

        public LocalTempFile(string path, Sha256 fileSha256, long fileSize, IFileSystem fs)
        {
            Path = path;
            FileSha256 = fileSha256;
            FileSize = fileSize;
            _fs = fs;
        }

        public string Path { get; }
        public Sha256 FileSha256 { get; }
        public long FileSize { get; }

        public Stream OpenRead() => _fs.File.OpenRead(Path);

        public ValueTask DisposeAsync()
        {
            if (_fs.File.Exists(Path))
            {
                try
                {
                    _fs.File.Delete(Path);
                }
                catch
                {
                    // Ignore best-effort cleanup errors
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
