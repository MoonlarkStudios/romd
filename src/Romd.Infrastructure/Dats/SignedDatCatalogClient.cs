using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.Configuration;
using Romd.Admin.Application.Source.Dat;
using Romd.Application.Common.Configuration;

namespace Romd.Infrastructure.Dats;

/// <summary>Runs the pinned Go/TUF reader with operator configuration, never request-supplied URLs or commands.</summary>
public sealed class SignedDatCatalogClient : ISignedDatCatalogClient
{
    private readonly IConfiguration _configuration;
    private readonly IRomdOptions _options;
    public SignedDatCatalogClient(IConfiguration configuration, IRomdOptions options)
    {
        _configuration = configuration;
        _options = options;
    }
    public bool Enabled => _configuration.GetValue<bool>("DatSubscriptions:Enabled");

    public Task<ErrorOr<byte[]>> FetchPlayStationAsync(CancellationToken ct) =>
        ReadAsync("candidate", ["--catalog", "redump/psx/discs", "--name", "Sony - PlayStation"], (path, token) => ReadCandidateAsync(path, "redump/psx/discs", null, "Sony - PlayStation", token), Failure(), ct);

    public Task<ErrorOr<byte[]>> FetchAsync(string catalogId, string systemId, string expectedName, CancellationToken ct) =>
        ReadAsync("candidate", ["--catalog", catalogId, "--name", expectedName],
            (path, token) => ReadCandidateAsync(path, catalogId, systemId, expectedName, token), Failure(), ct);

    public Task<ErrorOr<IReadOnlyList<PublishedDatCatalog>>> DiscoverAsync(CancellationToken ct) =>
        ReadAsync("catalogs", [], ReadCatalogsAsync, Failure(), ct);

    private async Task<ErrorOr<T>> ReadAsync<T>(string command, string[] arguments,
        Func<string, CancellationToken, Task<ErrorOr<T>>> read, Error failure, CancellationToken ct)
    {
        if (!Enabled) return failure;
        var executable = _configuration["DatSubscriptions:ReaderPath"];
        var root = _configuration["DatSubscriptions:RootPath"];
        var site = _configuration["DatSubscriptions:Site"];
        var bundle = _configuration["DatSubscriptions:BundlePath"];
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(root)
            || string.IsNullOrWhiteSpace(site) == string.IsNullOrWhiteSpace(bundle)) return failure;
        string? attempt = null;
        try
        {
            // Separate caches when trust or publisher changes; a working cache is never reset on failure.
            var rootHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(root, ct)));
            var identity = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rootHash + "\n" + site + "\n" + bundle)));
            var directory = Path.Combine(_options.DataDirectory, "dat-subscriptions", identity);
            Directory.CreateDirectory(directory);
            // OS-owned lock survives process failure; keep the inode for the next owner.
            using var gate = new FileStream(Path.Combine(directory, "reader.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            attempt = Path.Combine(directory, "candidate-" + Guid.NewGuid().ToString("N"));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { command, "--root", root, "--cache", Path.Combine(directory, "cache"),
                "--out", attempt, string.IsNullOrWhiteSpace(bundle) ? "--site" : "--bundle",
                string.IsNullOrWhiteSpace(bundle) ? site! : bundle! }.Concat(arguments)) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null) return failure;
            // Drain without retaining unbounded diagnostics or reflecting paths/secrets into the UI.
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                await Task.WhenAll(stderr, stdout);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                if (ct.IsCancellationRequested) throw;
                return failure;
            }
            if (process.ExitCode != 0) return failure;
            return await read(attempt, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return failure;
        }
        finally
        {
            // Cleanup must not replace a successful read or mask cancellation.
            try { if (attempt is not null && Directory.Exists(attempt)) Directory.Delete(attempt, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    private static async Task<ErrorOr<byte[]>> ReadCandidateAsync(string directory, string catalogId, string? systemId, string expectedName, CancellationToken ct)
    {
        var infoPath = Path.Combine(directory, "candidate.json");
        var datPath = Path.Combine(directory, "candidate.dat");
        if (new FileInfo(infoPath).Length > 16384 || new FileInfo(datPath).Length > DatReplacementReview.MaximumBytes) return Failure();
        using var info = JsonDocument.Parse(await File.ReadAllBytesAsync(infoPath, ct));
        var bytes = await File.ReadAllBytesAsync(datPath, ct);
        var metadata = info.RootElement;
        if (metadata.GetProperty("catalogId").GetString() != catalogId
            || metadata.GetProperty("name").GetString() != expectedName
            || (systemId is not null && (!metadata.TryGetProperty("systemId", out var system) || system.GetString() != systemId))
            || metadata.GetProperty("bytes").GetInt32() != bytes.Length
            || metadata.GetProperty("publicationVersion").GetInt64() < 1
            || metadata.GetProperty("sha256").GetString() != Convert.ToHexStringLower(SHA256.HashData(bytes))) return Failure();
        return bytes;
    }

    private static async Task<ErrorOr<IReadOnlyList<PublishedDatCatalog>>> ReadCatalogsAsync(string directory, CancellationToken ct)
    {
        if (new FileInfo(Path.Combine(directory, "catalog.json")).Length > 4 << 20) return Failure();
        using var index = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, "catalog.json"), ct));
        return SignedDatCatalogIndex.Read(index.RootElement).ToArray();
    }

    private static Error Failure() => Error.Failure("DatSubscription.CheckFailed",
        "The signed catalog could not be verified or downloaded. Your current catalog is unchanged. Try checking again.");
}
