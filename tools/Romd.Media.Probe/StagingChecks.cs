using Romd.Application.Common.Content;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Storage;

namespace Romd.Media.Probe;

/// <summary>Opt-in, real-process staging ownership/crash qualification; no game files needed.</summary>
internal static class StagingChecks
{
    internal static async Task RunAsync(string[] args)
    {
        if (args is ["--staging-probe"])
            args = ["--staging-probe", Path.Combine(Path.GetTempPath(), "romd-staging-result-" + Guid.NewGuid().ToString("N") + ".json")];
        if (args.Length != 2)
            throw new ArgumentException("Usage: --staging-probe <report.json> | --staging-child <scratch-root>");
        if (args[0] is "--staging-child" or "--staging-child-partial")
        {
            using Stream source = args[0] == "--staging-child-partial" ? new PartialSource(args[1]) : new MemoryStream(new byte[131072]);
            await using var file = await Factory(args[1]).CreateAsync(source);
            Console.WriteLine(file.Path);
            await Console.Out.FlushAsync();
            await Task.Delay(Timeout.Infinite);
            return;
        }
        if (args[0] != "--staging-probe")
            throw new ArgumentException("Unknown staging probe mode.");
        string root = Path.Combine(Path.GetTempPath(), "romd-staging-process-" + Guid.NewGuid().ToString("N"));
        var checks = new List<string>();
        File.WriteAllText(args[1], JsonSerializer.Serialize(new { Status = "in-progress", Checks = checks }));
        try
        {
            foreach (string mode in new[] { "--staging-child", "--staging-child-partial" })
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                start.ArgumentList.Add(mode);
                start.ArgumentList.Add(root);
                using var child = Process.Start(start) ?? throw new IOException("Could not start lease owner.");
                try
                {
                    string? owned = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    if (string.IsNullOrWhiteSpace(owned) || !File.Exists(owned))
                        throw new IOException("Child did not publish its staged file: " + await child.StandardError.ReadToEndAsync());
                    var contender = Factory(root);
                    try
                    {
                        await using var unexpected = await contender.CreateAsync(new MemoryStream([1]));
                        throw new InvalidOperationException("Another process reused a live staging slot.");
                    }
                    catch (ResourceCapacityException error) when (error.Message == "Temporary staging capacity or free disk space is exhausted; retry later.")
                    { checks.Add(mode + ": separate process cannot reuse a live slot or exceed its byte budget"); }
                    Check.That(File.ReadAllBytes(owned).Length == (mode == "--staging-child" ? 131072 : 65536), "Live process staging payload remains intact");
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Check.That(File.Exists(owned), "Abrupt termination left a payload to reclaim");
                    checks.Add(mode + ": killed owner leaves its reservation and bytes for guarded reclamation");
                    await using (var recovered = await Factory(root).CreateAsync(new MemoryStream([4, 5])))
                    {
                        Check.That(recovered.FileSize == 2 && File.ReadAllBytes(recovered.Path).SequenceEqual(new byte[] { 4, 5 }),
                            "Crash recovery deletes old bytes before slot reuse");
                        checks.Add(mode + ": fresh factory reclaims the dead owner and safely reuses capacity");
                    }
                    Check.That(!Directory.EnumerateFiles(root, "*.data", SearchOption.AllDirectories).Any(), "No payload remains after disposal");
                    checks.Add(mode + ": normal disposal removes the replacement payload");
                }
                finally
                {
                    if (!child.HasExited)
                        child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
            }
            File.WriteAllText(args[1], JsonSerializer.Serialize(new
            {
                Status = "passed",
                Runtime = RuntimeInformation.RuntimeIdentifier,
                Checks = checks
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Staging process checks passed ({checks.Count}); evidence: {args[1]}");
        }
        catch (Exception error)
        {
            File.WriteAllText(args[1], JsonSerializer.Serialize(new { Status = "failed", Error = error.ToString(), Checks = checks }));
            throw;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static LocalTempFileFactory Factory(string root)
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(root);
        options.MaxUploadBytes.Returns(131072);
        return new(options, new FileSystem(), new TempStorageOptions
        {
            MaximumFiles = 1,
            MaximumBytes = 131072,
            MinimumFreeBytes = 0,
            AdmissionTimeoutSeconds = 1
        });
    }

    private sealed class PartialSource(string root) : Stream
    {
        private bool _first = true;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_first)
            { _first = false; buffer.Span[..65536].Fill(7); return 65536; }
            Console.WriteLine(Directory.EnumerateFiles(root, "*.data", SearchOption.AllDirectories).Single());
            await Console.Out.FlushAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
