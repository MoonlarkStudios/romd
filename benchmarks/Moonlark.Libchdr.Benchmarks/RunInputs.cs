using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Moonlark.Libchdr.Internal;

namespace Moonlark.Libchdr.Benchmarks;

internal static class RunInputs
{
    internal static string Write(string root, string path)
    {
        string commit = Command(root, "git", "rev-parse", "HEAD").Trim();
        if (commit.Length != 40 || !commit.All(Uri.IsHexDigit)
            || Command(root, "git", "status", "--porcelain", "--untracked-files=normal").Length != 0)
            throw new InvalidOperationException("Benchmark runs require a clean source commit.");
        string runtimeRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        // Recorded, not compared: the repository pins only the SDK band.
        string sdk = Command(root, Path.Combine(runtimeRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), "--version").Trim();
        string runId = Guid.NewGuid().ToString("N");
        var receipt = new
        {
            SchemaVersion = 2,
            RunId = runId,
            SourceCommit = commit,
            Sdk = sdk,
            Runtime = RuntimeInformation.FrameworkDescription,
            Rid = NativePlatform.Current().Rid,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            OS = RuntimeInformation.OSDescription,
            Files = Files(root),
            Policy = new
            {
                Launches = 3,
                WarmupIterations = 6,
                MinIterations = 15,
                MaxIterations = 50,
                Outliers = "DontRemove",
                ReadAheadBytes = 0,
                Confidence = 0.95,
                BenchmarkMatrix.Fixtures,
                BenchmarkMatrix.Operations
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(receipt, BenchmarkMatrix.JsonOptions));
        return runId;
    }

    internal static void VerifySourceIdentity(string expected, string actual, string status)
    {
        if (expected.Length != 40 || !expected.All(Uri.IsHexDigit) || expected != actual || status.Length != 0)
            throw new InvalidDataException("Benchmark source differs from the original clean commit.");
    }

    internal static void VerifySource(string root, string path)
    {
        using JsonDocument receipt = JsonDocument.Parse(File.ReadAllText(path));
        string expected = receipt.RootElement.GetProperty("SourceCommit").GetString()
            ?? throw new InvalidDataException("Missing source identity.");
        VerifySourceIdentity(expected, Command(root, "git", "rev-parse", "HEAD").Trim(),
            Command(root, "git", "status", "--porcelain", "--untracked-files=normal"));
    }

    internal static void VerifyAssets(string root, string path, string parentHash)
    {
        if (Hash(path) != parentHash)
            throw new InvalidDataException("Parent input receipt changed.");
        using JsonDocument receipt = JsonDocument.Parse(File.ReadAllText(path));
        Dictionary<string, string> expected = receipt.RootElement.GetProperty("Files").Deserialize<Dictionary<string, string>>()
            ?? throw new InvalidDataException("Missing run input hashes.");
        Dictionary<string, string> actual = AssetFiles(root);
        if (expected.Count != actual.Count + 2 || !expected.ContainsKey("benchmark-assembly") || !expected.ContainsKey("library-assembly")
            || actual.Any(file => !expected.TryGetValue(file.Key, out string? hash) || hash != file.Value))
            throw new InvalidDataException("Benchmark native or fixture inputs changed from the parent receipt.");
    }

    internal static void Verify(string root, string path)
    {
        using JsonDocument receipt = JsonDocument.Parse(File.ReadAllText(path));
        Dictionary<string, string> expected = receipt.RootElement.GetProperty("Files").Deserialize<Dictionary<string, string>>()
            ?? throw new InvalidDataException("Missing run input hashes.");
        Dictionary<string, string> actual = Files(root);
        if (expected.Count != actual.Count || actual.Any(file => !expected.TryGetValue(file.Key, out string? hash) || hash != file.Value))
            throw new InvalidDataException("Benchmark input bytes changed from the run receipt.");
    }

    internal static Dictionary<string, string> Files(string root)
    {
        Dictionary<string, string> hashes = AssetFiles(root);
        hashes.Add("benchmark-assembly", Hash(typeof(ReadBenchmarks).Assembly.Location));
        hashes.Add("library-assembly", Hash(typeof(ChdFile).Assembly.Location));
        return hashes;
    }

    private static Dictionary<string, string> AssetFiles(string root)
    {
        (string rid, string filename) = NativePlatform.Current();
        string native = Path.Combine("artifacts", "native", "libchdr", rid);
        string fixtures = Path.Combine("artifacts", "fixtures", "libchdr-final");
        string[] files = [Path.Combine(native, "native", filename), Path.Combine(native, "build-manifest.json"),
            Path.Combine(fixtures, "fixtures-manifest.json"), .. BenchmarkMatrix.Fixtures.Select(fixture => Path.Combine(fixtures, fixture + ".chd"))];
        return files.ToDictionary(file => file.Replace(Path.DirectorySeparatorChar, '/'), file => Hash(Path.Combine(root, file)), StringComparer.Ordinal);
    }

    internal static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Command(string root, string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new IOException("Cannot start provenance command.");
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Provenance command timed out.");
        }
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0)
            throw new IOException("Provenance command failed: " + error);
        return output;
    }
}
