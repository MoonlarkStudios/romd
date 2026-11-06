using System.Diagnostics;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Ingestion.Extraction;
using Romd.Infrastructure.Import;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Storage;

namespace Romd.Media.Probe;

internal static class ExtractionChecks
{
    internal static async Task RunAsync(string[] args)
    {
        if (args.Length == 3 && args[0] == "--extraction-child")
        {
            await Step(new PausingExtractor(args[2])).ExtractRecursivelyAsync(args[1], default);
            await PauseAsync(); // Completed extraction, before the job's classification checkpoint.
            return;
        }
        if (args.Length is < 1 or > 2 || args[0] != "--extraction-probe")
            throw new ArgumentException("Usage: --extraction-probe [report.json]");
        string report = args.Length == 2 ? args[1] : Path.Combine(Path.GetTempPath(), $"romd-extraction-{Guid.NewGuid():N}.json");
        string root = Directory.CreateTempSubdirectory("romd-extraction-process-").FullName;
        var checks = new List<string>();
        File.WriteAllText(report, JsonSerializer.Serialize(new { Status = "in-progress", Checks = checks }));
        try
        {
            byte[] payload = new byte[512 * 1024];
            new Random(527).NextBytes(payload);
            foreach (string boundary in new[] { "partial", "decoded", "completed" })
            {
                string workspace = Directory.CreateDirectory(Path.Combine(root, boundary)).FullName;
                string source = Path.Combine(workspace, "game.zip");
                using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
                using (var entry = zip.CreateEntry("game.rom").Open())
                    entry.Write(payload);
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                foreach (string argument in new[] { "--extraction-child", workspace, boundary })
                    start.ArgumentList.Add(argument);
                using var child = Process.Start(start) ?? throw new IOException("Could not start extraction worker.");
                try
                {
                    string? ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    Check.That(ready == "ready", "Child reached the requested extraction boundary");
                    string output = Path.Combine(workspace, "game", "game.rom");
                    Check.That(File.Exists(workspace + ".extraction.json"), "Recovery journal exists before extraction mutation");
                    if (boundary == "partial")
                    {
                        long length = new FileInfo(output).Length;
                        Check.That(length > 0 && length < payload.Length, "Worker has actually written partial output");
                    }
                    Check.That(File.Exists(source) == (boundary != "completed"), "Source retention matches publication boundary");
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    checks.Add(boundary + ": abrupt worker death leaves checkpoint and recoverable bytes");
                    await Step(new ZipArchiveExtractor()).ExtractRecursivelyAsync(workspace, default);
                    Check.That(File.ReadAllBytes(output).SequenceEqual(payload), "Recovered member exactly matches original fixture");
                    Check.That(!File.Exists(source), "Published source cleanup completes");
                    Check.That(Directory.GetDirectories(workspace).Length == 1, "Recovery uses the same output path without duplicate trees");
                    await Step(new ZipArchiveExtractor()).ExtractRecursivelyAsync(workspace, default);
                    Check.That(File.ReadAllBytes(output).SequenceEqual(payload), "Completed phase replay leaves exact bytes unchanged");
                    checks.Add(boundary + ": fresh extractor restores exact bytes and completes idempotently");
                }
                finally
                {
                    if (!child.HasExited)
                        child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
            }
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                Status = "passed",
                Runtime = RuntimeInformation.RuntimeIdentifier,
                Checks = checks
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Extraction process checks passed ({checks.Count}); evidence: {report}");
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { Status = "failed", Error = error.ToString(), Checks = checks }));
            throw;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ArchiveExtractionStep Step(IArchiveExtractor extractor) => new(
        new ArchiveExtractorResolver([extractor]), new FileSystem(),
        new ArchiveExtractionLimits { SpaceFloorBytes = 0 }, NullLogger<ArchiveExtractionStep>.Instance);

    private static async Task PauseAsync()
    {
        Console.WriteLine("ready");
        await Console.Out.FlushAsync();
        await Task.Delay(Timeout.Infinite);
    }

    private sealed class PausingExtractor(string boundary) : IArchiveExtractor
    {
        private readonly ZipArchiveExtractor _zip = new();
        public IReadOnlyList<string> SupportedExtensions => _zip.SupportedExtensions;
        public bool CanHandle(string filename) => _zip.CanHandle(filename);
        public IAsyncEnumerable<ArchiveEntry> EnumerateAsync(Stream source, CancellationToken ct = default) => _zip.EnumerateAsync(source, ct);
        public Task<Stream> ExtractAsync(Stream source, string entry, CancellationToken ct = default) => _zip.ExtractAsync(source, entry, ct);
        public async Task<IReadOnlyList<ExtractedEntry>> ExtractAllAsync(Stream source, string target, IExtractionQuota quota, CancellationToken ct = default)
        {
            var result = await _zip.ExtractAllAsync(source, target, boundary == "partial" ? new PausingQuota(quota) : quota, ct);
            if (boundary == "decoded")
                await PauseAsync();
            return result;
        }
    }

    private sealed class PausingQuota(IExtractionQuota inner) : IExtractionQuota
    {
        private int _writes;
        public bool TryChargeEntry() => inner.TryChargeEntry();
        public bool TryChargeBytes(long bytes)
        {
            if (++_writes == 2)
                PauseAsync().GetAwaiter().GetResult();
            return inner.TryChargeBytes(bytes);
        }
    }
}
