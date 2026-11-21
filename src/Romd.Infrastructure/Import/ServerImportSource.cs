using System.IO.Abstractions;
using ErrorOr;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Application.Common.Configuration;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Import;

public sealed class ServerImportSource(IRomdOptions options, IPathValidator validator, IFileSystem fileSystem) : IServerImportSource
{
    private static readonly TimeSpan _settleTime = TimeSpan.FromSeconds(60);
    private static readonly HashSet<string> _temporaryExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".part", ".partial", ".tmp", ".download", ".crdownload" };

    public ErrorOr<ServerImportScan> Scan(string directory, DateTimeOffset now, CancellationToken ct)
    {
        var allowed = validator.ValidateImportRoot(directory);
        if (allowed.IsError)
            return allowed.Errors;

        var enumeration = new ImportSourceEnumerator(validator).Enumerate(directory, ImportPreflight.MaxFileCount, ct,
            include: path => !Path.GetFileName(path).StartsWith('.'));
        if (enumeration.IsError)
            return enumeration.Errors;

        var (observed, skipped) = enumeration.Value;
        var ready = new List<ServerImportFile>(observed.Count);
        int waiting = 0;
        string? warning = skipped.Count == 0 ? null : skipped[0].Reason;
        var matureAt = now.UtcDateTime - _settleTime;
        foreach (var file in observed)
        {
            ct.ThrowIfCancellationRequested();
            if (file.RelativePath.Length > JobItem.MaxFileNameLength)
            {
                warning ??= $"The path for '{file.RelativePath}' is too long to import.";
                continue;
            }

            if (_temporaryExtensions.Contains(Path.GetExtension(file.RelativePath)) ||
                file.SizeBytes == 0 || file.LastWriteTimeUtc > matureAt)
            {
                waiting++;
                continue;
            }

            ready.Add(new ServerImportFile(file.RelativePath, file.SizeBytes, file.LastWriteTimeUtc));
        }

        return new ServerImportScan(ready, waiting, warning);
    }

    public ErrorOr<Success> ValidateCapacity(IReadOnlyList<ServerImportFile> files) =>
        PathImportFiles.CheckCapacity(options.DataDirectory, fileSystem, files.Count, files.Sum(file => file.Bytes));
}
