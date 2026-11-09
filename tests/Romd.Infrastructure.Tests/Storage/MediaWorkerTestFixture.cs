using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Storage;
using Romd.Media.Cd;
using Romd.Media.Chd;
using Romd.Media.Inspection;
using Romd.Media.PlayStation;
using Shouldly;

namespace Romd.Infrastructure.Tests.Storage;

/// <summary>A real child with trusted startup code and injected process faults; no host decoder seam.</summary>
internal sealed class MediaWorkerTestFixture : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("romd-media-child-fixture-").FullName;
    private readonly string _startupSourceHash;
    private readonly string _faultSourceHash;
    public MediaProcessingOptions Options { get; }

    public MediaWorkerTestFixture(string startupMode = "valid", string inspectionMode = "valid")
    {
        string packaged = Path.Combine(AppContext.BaseDirectory, "media-worker");
        foreach (string source in Directory.EnumerateFiles(packaged))
            File.Copy(source, Path.Combine(_directory, Path.GetFileName(source)), overwrite: true);
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "src", "Romd.Media.Worker", "Program.cs")))
            root = Directory.GetParent(root)?.FullName;
        string repository = root.ShouldNotBeNull();
        string program = File.ReadAllText(Path.Combine(repository, "src", "Romd.Media.Worker", "Program.cs"));
        _startupSourceHash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(program)));
        program = "using System;\nusing System.IO;\nusing System.Linq;\nusing System.Threading;\nusing System.Threading.Tasks;\n" + program;
        program = program.Replace("return await RunAsync(args, CancellationToken.None);", """
            if (args.Length == 2 && args[0] == "descendant")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            }
            return await RunAsync(args, CancellationToken.None);
            """, StringComparison.Ordinal);
        string injected = FaultModes.Replace("__STARTUP_MODE__", JsonSerializer.Serialize(startupMode), StringComparison.Ordinal)
            .Replace("__INSPECTION_MODE__", JsonSerializer.Serialize(inspectionMode), StringComparison.Ordinal)
            .Replace("__RELEASE_PATH__", JsonSerializer.Serialize(Path.Combine(_directory, "released")), StringComparison.Ordinal)
            .Replace("__CHD_IDENTITY_PATH__", JsonSerializer.Serialize(Path.Combine(_directory, "candidate-identity.json")), StringComparison.Ordinal)
            .Replace("__TOOL_IDENTITY_PATH__", JsonSerializer.Serialize(Path.Combine(_directory, "tool-identity.json")), StringComparison.Ordinal);
        program = program.Replace("    MediaWorkerReply reply;", injected + "\n    MediaWorkerReply reply;", StringComparison.Ordinal);
        program += "\n" + File.ReadAllText(Path.Combine(repository, "tests", "Romd.Infrastructure.Tests", "Storage", "FixtureMarkerPublication.cs"));
        _faultSourceHash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(program)));
        using var references = new FixtureCompilationReferences(
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Append(typeof(MediaWorkerProtocol).Assembly.Location));
        var compilation = CSharpCompilation.Create("Romd.Media.Worker", [CSharpSyntaxTree.ParseText(program)], references.References,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        string worker = Path.Combine(_directory, "Romd.Media.Worker.dll");
        var emitted = compilation.Emit(worker);
        emitted.Success.ShouldBeTrue(string.Join("\n", emitted.Diagnostics));
        Options = new() { WorkerPath = worker, MaxConcurrent = 1, AdmissionTimeoutSeconds = 1, WallTimeoutSeconds = 10, CpuTimeoutSeconds = 10, ChdEncodeWallTimeoutSeconds = 10 };
    }

    public Input Source(byte[] bytes, string mode = "valid") => new(Path.Combine(_directory, mode + ".rvz"), bytes);
    public RvzVerifier Verifier() => new(Options, new MediaWorkerSupervisor(Options));
    public MediaWorkerSupervisor Supervisor() => new(Options);
    /// <summary>Synthetic descriptor for injected wire replies only; never a loadable native package.</summary>
    public ChdNativeCandidate ChdCandidate()
    {
        string build = Path.Combine(_directory, "native-build");
        Directory.CreateDirectory(Path.Combine(build, "native"));
        File.WriteAllBytes(Path.Combine(build, "native", "libmoonlark_chdr.so"), [1, 2, 3]);
        var identity = new ChdNativeIdentity(Romd.Media.Chd.NativeRuntimeIdentifier.Current!, "libmoonlark_chdr.so", 3,
            Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 })), new string('d', 40), "fixture-upstream", 1,
            "fixture/v1", new string('e', 64), true, true, true);
        File.WriteAllText(Path.Combine(_directory, "candidate-identity.json"), MediaWorkerProtocol.WriteNativeIdentity(identity).GetRawText());
        return new(build, identity);
    }
    /// <summary>Synthetic tool descriptor for wire controls; never passed to a real tool verifier or encoder.</summary>
    public ChdmanCandidate ToolCandidate()
    {
        string build = Path.Combine(_directory, "tool-build");
        byte[] binaryBytes = [7, 8, 9];
        Directory.CreateDirectory(Path.Combine(build, "native"));
        File.WriteAllBytes(Path.Combine(build, "native", "chdman"), binaryBytes);
        // Encoded replies come only from Linux tools, so the wire controls name a Linux build of this architecture.
        string rid = "linux-" + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var identity = new ChdmanToolIdentity(rid, Romd.Media.Chd.ChdmanPin.Tag, Romd.Media.Chd.ChdmanPin.SourceSha256, 1,
            binaryBytes.Length, Convert.ToHexStringLower(SHA256.HashData(binaryBytes)));
        File.WriteAllText(Path.Combine(_directory, "tool-identity.json"), MediaWorkerProtocol.WriteToolIdentity(identity).GetRawText());
        return new(build, identity);
    }
    public void Release() => File.WriteAllText(Path.Combine(_directory, "released"), "released");
    public void ChdHandshakeMode(string mode) => File.WriteAllText(Path.Combine(_directory, "released") + ".chd-handshake-mode", mode);
    public void ExportTo(string destination, byte[] original)
    {
        if (!Path.IsPathFullyQualified(destination) || Directory.Exists(destination))
            throw new ArgumentException("Fault export requires a fresh absolute directory.", nameof(destination));
        Directory.CreateDirectory(destination);
        foreach (string source in Directory.EnumerateFiles(_directory))
            File.Copy(source, Path.Combine(destination, Path.GetFileName(source)), overwrite: false);
        File.WriteAllBytes(Path.Combine(destination, "original.iso"), original);
        using var assembly = File.OpenRead(Options.WorkerPath);
        using var pe = new System.Reflection.PortableExecutable.PEReader(assembly);
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var manifest = new
        {
            startupSourceSha256 = _startupSourceHash,
            faultSourceSha256 = _faultSourceHash,
            workerBuild = "romd-media-worker/mvid:" + metadata.GetGuid(metadata.GetModuleDefinition().Mvid),
            exportedDecoderBuild = MediaInspectionService.DecoderBuild,
            original = new
            {
                file = "original.iso",
                bytes = original.Length,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(original)),
                sha1 = Convert.ToHexStringLower(SHA1.HashData(original)),
                md5 = Convert.ToHexStringLower(MD5.HashData(original)),
                crc32 = System.IO.Hashing.Crc32.HashToUInt32(original).ToString("x8", System.Globalization.CultureInfo.InvariantCulture)
            },
            native = new
            {
                defaultFile = "native-memory.rvz",
                controlFile = "native-control.rvz",
                sidecarSuffix = ".native-probe",
                requestedBytes = (1L << 30) + (64L << 20),
                smallTouchedBytes = 65536,
                defaultDataBytes = 1L << 30,
                controlDataBytes = 2L << 30,
                gcHeapBytes = 512L << 20
            },
            managed = new
            {
                defaultFile = "managed-reject.rvz",
                controlFile = "managed-control.rvz",
                sidecarSuffix = ".managed-probe",
                requestedBytes = 536870913,
                defaultGcHeapBytes = 512L << 20,
                controlGcHeapBytes = 1L << 30,
                dataBytes = 2L << 30,
                addressSpaceBytes = 32L << 30
            },
            cpu = new
            {
                file = "cpu.rvz",
                cpuSeconds = 1,
                wallSeconds = 10,
                scope = "Combined supervisor/kernel deadline control; no kernel-only attribution."
            },
            files = Directory.EnumerateFiles(destination).Order(StringComparer.Ordinal).Select(path => new
            {
                file = Path.GetFileName(path),
                bytes = new FileInfo(path).Length,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
            }).ToArray()
        };
        using var report = new FileStream(Path.Combine(destination, "fixture-manifest.json"), FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(report, manifest, new JsonSerializerOptions { WriteIndented = true });
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    public static async Task<int> WaitForPidAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path) && int.TryParse(await File.ReadAllTextAsync(path, ct),
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid) && pid > 0)
                return pid;
            await Task.Delay(10, ct);
        }
    }
    public static bool IsRunning(int pid)
    {
        try
        { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    internal sealed class Input : ITempFile
    {
        private bool _quarantined;
        public bool TryBeginChildUse()
        {
            using var marker = new FileStream(Path + ".quarantine", FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            marker.Flush(flushToDisk: true);
            return true;
        }
        public bool TryEndChildUse() { File.Delete(Path + ".quarantine"); return true; }
        public bool Quarantine() { _quarantined = true; return TryBeginChildUse(); }

        public Input(string path, byte[] bytes)
        {
            Path = path;
            File.WriteAllBytes(path, bytes);
            FileSize = bytes.Length;
            FileSha256 = Sha256.FromSpan(SHA256.HashData(bytes));
        }
        public string Path { get; }
        public long FileSize { get; }
        public Sha256 FileSha256 { get; }
        public Stream OpenRead() => File.OpenRead(Path);
        public ValueTask DisposeAsync() { if (!_quarantined) File.Delete(Path); return ValueTask.CompletedTask; }
    }

    private const string FaultModes = """
        string mode = arguments.Length == 1 ? __STARTUP_MODE__ : __INSPECTION_MODE__ == "valid" ? Path.GetFileNameWithoutExtension(arguments[1]) : __INSPECTION_MODE__;
        if (arguments[0] == "handshake-chd" && File.Exists(__RELEASE_PATH__ + ".chd-handshake-mode")) mode = await File.ReadAllTextAsync(__RELEASE_PATH__ + ".chd-handshake-mode");
        if (arguments.Length > 1) await Romd.Infrastructure.Tests.Storage.FixtureMarkerPublication.PublishAsync(arguments[1] + ".pid",
            stream => stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture))).AsTask());
        if (mode == "gated") while (!File.Exists(__RELEASE_PATH__)) await Task.Delay(10);
        if (mode == "encoded-hang" && arguments[0] == "encode")
        {
            if (!NativePriority.TryReduce(out string priorityKind, out int priorityValue)) return 64;
            var encoded = await MediaEncodingService.EncodeAsync(arguments[1], arguments[2],
                long.Parse(arguments[3], CultureInfo.InvariantCulture), long.Parse(arguments[4], CultureInfo.InvariantCulture),
                long.Parse(arguments[5], CultureInfo.InvariantCulture), long.Parse(arguments[6], CultureInfo.InvariantCulture),
                Romd.Media.Nintendo.NintendoDiscKind.GameCube, priorityKind, priorityValue, CancellationToken.None);
            if (encoded.Status != MediaWorkerStatus.Encoded) return 20;
            await File.WriteAllTextAsync(arguments[1] + ".payload-ready", "ready");
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        if (mode is "descendant-exit" or "descendant-hang")
        {
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add(typeof(NativeSession).Assembly.Location);
            start.ArgumentList.Add("descendant");
            start.ArgumentList.Add(arguments[1]);
            using var descendant = System.Diagnostics.Process.Start(start)!;
            await Romd.Infrastructure.Tests.Storage.FixtureMarkerPublication.PublishAsync(arguments[1] + ".descendant.pid",
                stream => stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(descendant.Id.ToString(CultureInfo.InvariantCulture))).AsTask());
            if (mode == "descendant-exit") return 17;
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        if (mode == "hang") await Task.Delay(Timeout.InfiniteTimeSpan);
        if (mode == "cpu") { while (true) System.Threading.Thread.SpinWait(10000); }
        if (mode == "crash") return 17;
        if (mode == "private-diagnostic") { Console.Error.Write("fixture-private-content"); return 17; }
        if (mode == "runtime-oom-diagnostic") { Console.Error.Write("Out of memory."); return 17; }
        if (mode == "empty") return 0;
        if (mode == "malformed-output") { Console.Write("{}"); return 0; }
        if (mode == "stdout-overflow")
        {
            await Console.OpenStandardOutput().WriteAsync(new byte[(arguments[0] == "inspect-chd-cd" ?
                MediaWorkerProtocol.MaximumCdOutputBytes : MediaWorkerProtocol.MaximumOutputBytes) + 1]);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        if (mode == "diagnostic-overflow")
        {
            await Console.OpenStandardError().WriteAsync(new byte[16385]);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        if (mode == "bad-heap") identity = identity with { GcHeapLimitBytes = identity.GcHeapLimitBytes / 2, GcAvailableBytes = identity.GcAvailableBytes / 2 };
        if (mode == "bad-build") identity = identity with { DecoderBuild = "invented" };
        if (mode.StartsWith("chd-encode-", StringComparison.Ordinal) || mode == "chd-extract-valid")
        {
            string toolJson = mode == "chd-encode-for-managed" ? await File.ReadAllTextAsync(__TOOL_IDENTITY_PATH__) :
                Environment.GetEnvironmentVariable("ROMD_CHDMAN_IDENTITY")!;
            using var toolDocument = JsonDocument.Parse(toolJson);
            if (!MediaWorkerProtocol.TryReadToolIdentity(toolDocument.RootElement, out var tool) || tool is null) return 64;
            if (mode == "chd-extract-valid")
            {
                // Authenticated operation-shape fixture, without a decoder or tool grandchild.
                byte[] sourceBytes = await File.ReadAllBytesAsync(arguments[1]);
                byte[] raw = Enumerable.Range(0, sourceBytes.Length).Select(index => (byte)(index % 251)).ToArray();
                var facts = new Romd.Media.Chd.ChdDvdExtractionMetadata(
                    Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(sourceBytes)), sourceBytes.LongLength,
                    Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(raw)),
                    Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(raw)),
                    Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(raw)),
                    System.IO.Hashing.Crc32.HashToUInt32(raw).ToString("x8", CultureInfo.InvariantCulture), raw.LongLength,
                    Romd.Media.Chd.ChdDvdEncodingService.ExtractionProfile, "unix-nice", 10);
                var execution = new ChdToolExecution(identity.ProcessId == int.MaxValue ? identity.ProcessId - 1 : identity.ProcessId + 1, 0, 0, 0);
                if (arguments[0] == "extract-chd-dvd") await File.WriteAllBytesAsync(arguments[2], raw);
                await Console.OpenStandardOutput().WriteAsync(MediaWorkerProtocol.Write(new(MediaWorkerStatus.Encoded, identity,
                    ToolIdentity: tool, ToolExecution: execution, ChdExtraction: facts)));
                return 0;
            }
            MediaWorkerReply injectedReply;
            if (mode is "chd-encode-source-rejection" or "chd-encode-wrong-source-path")
                injectedReply = new(MediaWorkerStatus.Malformed, identity, ChdEncodingRejection: new(
                    Romd.Media.Chd.ChdDvdEncodingStage.Source, Romd.Media.Chd.ChdDvdEncodingCause.SourceHashMismatch,
                    arguments[1] + (mode == "chd-encode-wrong-source-path" ? ".other" : "")));
            else if (mode == "chd-encode-candidate-rejection")
                injectedReply = new(MediaWorkerStatus.Malformed, identity, ChdEncodingRejection: new(
                    Romd.Media.Chd.ChdDvdEncodingStage.ToolVerification, Romd.Media.Chd.ChdDvdEncodingCause.CandidateRejected,
                    Path.Combine(Environment.GetEnvironmentVariable("ROMD_CHDMAN_DIRECTORY")!, "native", "chdman"), new(
                        Romd.Media.Chd.ChdRejectionStage.NativeVerification, "BinaryMissing",
                        Path.Combine(Environment.GetEnvironmentVariable("ROMD_CHDMAN_DIRECTORY")!, "native", "chdman"))));
            else if (mode == "chd-encode-wrong-output-path")
                injectedReply = new(MediaWorkerStatus.Malformed, identity, ToolIdentity: tool, ChdEncodingRejection: new(
                    Romd.Media.Chd.ChdDvdEncodingStage.Output, Romd.Media.Chd.ChdDvdEncodingCause.InvalidOutputHeader,
                    arguments[2] + ".other"));
            else if (mode is "chd-encode-tool-killed" or "chd-encode-external-kill" or "chd-encode-false-cpu-budget")
                // A joined CPU budget rejection, with consumed CPU evidence.
                injectedReply = new(mode == "chd-encode-external-kill" ? MediaWorkerStatus.Malformed : MediaWorkerStatus.LimitExceeded, identity, ToolIdentity: tool,
                    ToolExecution: new(identity.ProcessId == int.MaxValue ? identity.ProcessId - 1 : identity.ProcessId + 1, 128 + 9, 0, 0, mode == "chd-encode-tool-killed" ? 1_200_000_000 : 10_000),
                    ChdEncodingRejection: new(Romd.Media.Chd.ChdDvdEncodingStage.ToolExecution,
                        mode == "chd-encode-external-kill" ? Romd.Media.Chd.ChdDvdEncodingCause.ToolExitFailure : Romd.Media.Chd.ChdDvdEncodingCause.CpuBudgetExceeded,
                        Path.Combine(Environment.GetEnvironmentVariable("ROMD_CHDMAN_DIRECTORY")!, "native", "chdman")));
            else
            {
                // Header-only synthetic artifact. No compression, tool image or logical integrity is claimed.
                byte[] sourceBytes = await File.ReadAllBytesAsync(arguments[1]);
                string sourceHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(sourceBytes));
                byte[] artifact = new byte[512]; "MComprHD"u8.CopyTo(artifact);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(artifact.AsSpan(8), 124);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(artifact.AsSpan(12), 5);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(artifact.AsSpan(16), 0x7a737464);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(artifact.AsSpan(32), (ulong)sourceBytes.LongLength);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(artifact.AsSpan(56), 16384);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(artifact.AsSpan(60), 2048);
                if (arguments[0] == "encode-chd") await File.WriteAllBytesAsync(arguments[2], artifact);
                string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(artifact));
                string md5 = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(artifact));
                string sha1 = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(artifact));
                var header = new Romd.Media.Chd.ChdHeaderFacts(artifact.Length, (ulong)sourceBytes.LongLength, 16384, 2048,
                    checked((uint)(sourceBytes.LongLength / 16384 + (sourceBytes.LongLength % 16384 == 0 ? 0 : 1))), 0x7a737464, 0, 0, 0);
                var facts = new Romd.Media.Chd.ChdDvdArtifactMetadata(sourceHash, sourceBytes.LongLength, sourceBytes.LongLength,
                    hash, md5, sha1, artifact.Length, header, Romd.Media.Chd.ChdDvdCanonicalRecipe.Id,
                    Romd.Media.Chd.ChdDvdCanonicalRecipe.EncoderProfile, "unix-nice", 10);
                // These are intentionally synthetic scalars; this fixture never starts an encoder grandchild.
                var execution = new ChdToolExecution(identity.ProcessId == int.MaxValue ? identity.ProcessId - 1 : identity.ProcessId + 1, 0, 104, 88);
                if (mode == "chd-encode-wrong-source-hash") facts = facts with { SourceSha256 = new string('a', 64) };
                if (mode == "chd-encode-wrong-source-bytes") facts = facts with
                {
                    SourceBytes = facts.SourceBytes + 2048, LogicalBytes = facts.LogicalBytes + 2048,
                    Header = header with { LogicalBytes = header.LogicalBytes + 2048, HunkCount = header.HunkCount + 1 }
                };
                if (mode == "chd-encode-over-cap") facts = facts with
                {
                    ArtifactBytes = long.Parse(arguments[6], CultureInfo.InvariantCulture) + 1,
                    Header = header with { ArtifactBytes = long.Parse(arguments[6], CultureInfo.InvariantCulture) + 1 }
                };
                if (mode == "chd-encode-wrong-profile") facts = facts with { EncodingProfile = "dvd;unit=2048;hunk=32768;codec=zstd;processors=1" };
                if (mode == "chd-encode-wrong-recipe") facts = facts with { RecipeVersion = "chd-dvd-experiment/v2" };
                if (mode == "chd-encode-legacy-recipe") facts = facts with { RecipeVersion = "chd-dvd-experiment/v1" };
                if (mode == "chd-encode-historical-v1") facts = facts with { RecipeVersion = "chd-dvd-canonical/v1",
                    EncodingProfile = "dvd;unit=2048;hunk=16384;codec=zlib;processors=1", Header = header with { Codec0 = 0x7a6c6962 } };
                if (mode == "chd-encode-wrong-pin") tool = tool with { MameSourceSha256 = new string('a', 64) };
                if (mode == "chd-encode-wrong-binary") tool = tool with { BinarySha256 = new string('a', 64) };
                if (mode == "chd-encode-wrong-recipe-version") tool = tool with { RecipeVersion = tool.RecipeVersion + 1 };
                if (mode == "chd-encode-worker-pid") execution = execution with { ProcessId = identity.ProcessId };
                if (mode == "chd-encode-tool-nonzero") execution = execution with { ExitCode = 17 };
                if (mode == "chd-encode-diagnostic-overflow") execution = execution with { StandardOutputBytes = 131072, StandardErrorBytes = 131073 };
                if (mode == "chd-encode-normal-priority") facts = facts with { PriorityValue = 0 };
                injectedReply = mode == "chd-encode-rvz-reply"
                    ? new(MediaWorkerStatus.Encoded, identity, Encoding: new(sourceHash, sourceBytes.LongLength, sourceBytes.LongLength,
                        hash, md5, sha1, artifact.Length, Romd.Media.Rvz.RvzCanonicalRecipe.Id, Romd.Media.Rvz.RvzEncoder.EncoderBuild,
                        MediaWorkerProtocol.CanonicalEncodingProfile, "unix-nice", 10))
                    : new(MediaWorkerStatus.Encoded, identity, ChdEncoding: facts,
                        ToolIdentity: mode == "chd-encode-missing-tool" ? null : tool,
                        ToolExecution: mode == "chd-encode-missing-execution" ? null : execution);
                if (mode == "chd-encode-mixed-inspection") injectedReply = injectedReply with
                { Inspection = new(hash, artifact.Length, sourceHash, sha1, md5, "00000000", sourceBytes.LongLength, Convert.ToHexStringLower(sourceBytes.AsSpan(0, Math.Min(32, sourceBytes.Length))), "identity/v1") };
            }
            await Console.OpenStandardOutput().WriteAsync(MediaWorkerProtocol.Write(injectedReply));
            return mode == "chd-encode-worker-nonzero" ? 17 : 0;
        }
        if ((arguments[0] is "handshake-chd" or "inspect-chd" or "inspect-chd-cd") && mode.StartsWith("chd-", StringComparison.Ordinal) || mode == "chd-for-managed")
        {
            string nativeJson = mode == "chd-for-managed" ? await File.ReadAllTextAsync(__CHD_IDENTITY_PATH__) : Environment.GetEnvironmentVariable("ROMD_CHD_NATIVE_IDENTITY")!;
            using var nativeDocument = JsonDocument.Parse(nativeJson);
            if (!MediaWorkerProtocol.TryReadNativeIdentity(nativeDocument.RootElement, out var native) || native is null) return 64;
            if (mode == "chd-wrong-native") native = native with { BuildId = new string('a', 64) };
            MediaWorkerReply injectedReply;
            if (mode.StartsWith("chd-cd-", StringComparison.Ordinal))
            {
                byte[] artifact = await File.ReadAllBytesAsync(arguments[1]);
                string sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(artifact));
                string sha1 = new string('b', 40);
                long bytes = artifact.LongLength + (mode == "chd-cd-wrong-length" ? 1 : 0);
                if (mode == "chd-cd-wrong-native") native = native with { BuildId = new string('a', 64) };
                injectedReply = new(MediaWorkerStatus.Inspected, identity, NativeIdentity: native,
                    CdInspection: new(ChdCdInspectionService.Profile, ChdCdInspectionService.ProjectionProfile,
                        mode == "chd-cd-wrong-hash" ? new string('a', 64) : sha256,
                        5,false,new(bytes, 4 * 2448, 4 * 2448, 2448, 1, 0, 0, 0, 0), sha1, sha1, sha1, sha1,
                        [new(1, Moonlark.Libchdr.ChdCdTrackType.Mode2Raw, 0, 3, 1, 0, false, 0,
                            3 * 2352, sha256, sha1, new string('c', 32), "00000000")],
                        [new(0x43485432,1,Convert.ToHexStringLower(System.Text.Encoding.ASCII.GetBytes("TRACK:1 TYPE:MODE2_RAW SUBTYPE:NONE FRAMES:3 PREGAP:0 PGTYPE:MODE2_RAW PGSUB:NONE POSTGAP:0")))],null));
            }
            else if (mode is "chd-ready" or "chd-wrong-native") injectedReply = new(MediaWorkerStatus.Ready, identity, NativeIdentity: native);
            else if (mode is "chd-native-rejection" or "chd-wrong-rejection-path")
                injectedReply = new(MediaWorkerStatus.Malformed, identity, ChdRejection: new(Romd.Media.Chd.ChdRejectionStage.NativeVerification,
                    "BinaryMissing", Path.Combine(Environment.GetEnvironmentVariable("ROMD_CHD_NATIVE_DIRECTORY")!, "native", native.File)
                        + (mode == "chd-wrong-rejection-path" ? ".other" : "")));
            else if (mode is "chd-header-rejection" or "chd-wrong-source-path")
                injectedReply = new(MediaWorkerStatus.Malformed, identity, ChdRejection: new(Romd.Media.Chd.ChdRejectionStage.Header,
                    "TruncatedHeader", arguments[1] + (mode == "chd-wrong-source-path" ? ".other" : "")));
            else if (mode == "chd-rvz-reply")
            {
                var rvz = await MediaInspectionService.InspectRvzAsync(arguments[1], 16L << 20, 16L << 20, CancellationToken.None);
                injectedReply = new(rvz.Status, identity, rvz.Inspection);
            }
            else
            {
                byte[] artifact = await File.ReadAllBytesAsync(arguments[1]);
                string sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(artifact));
                string sha1 = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(artifact));
                long bytes = artifact.LongLength + (mode == "chd-wrong-artifact-length" ? 1 : 0);
                bool ps2 = mode is "chd-ps2-inspected" or "chd-ps2-malformed";
                uint logicalBytes = ps2 ? 131072U : 2048U;
                var facts = new MediaInspection(mode == "chd-wrong-artifact-hash" ? new string('a', 64) : sha256, bytes,
                    sha256, sha1, Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(artifact)), "00000000", logicalBytes, new string('0', 64), "chd-import/v1");
                injectedReply = new(MediaWorkerStatus.Inspected, identity, facts, NativeIdentity: native,
                    ChdInspection: new(5, new(bytes, logicalBytes, 2048, 2048, logicalBytes / 2048, 0, 0, 0, 0), sha1, sha1, sha1, sha1,
                        new("chd-dvd-metadata/v1", 1, 0x44564420, 1, 1, 0,
                            "c3ee3c942f2cbc35b3a4bec95ec700c2b9d21d5e90c7016b261504710490b406")),
                    Ps2Inspection: !ps2 ? null : mode == "chd-ps2-malformed"
                        ? new(Romd.Media.PlayStation.Ps2DvdIdentificationStatus.Malformed, Romd.Media.PlayStation.Ps2DvdIdentifier.ProfileVersion)
                        : new(Romd.Media.PlayStation.Ps2DvdIdentificationStatus.Identified, Romd.Media.PlayStation.Ps2DvdIdentifier.ProfileVersion,
                            new(131072, 64, 16, 24, 2048, 25, 60, 26, 512,
                                "cdrom0:\\SLUS_203.12;1", "cdrom0:\\SLUS_203.12;1", "SLUS_203.12", 0x100000, 1)));
            }
            await Console.OpenStandardOutput().WriteAsync(MediaWorkerProtocol.Write(injectedReply));
            return 0;
        }
        if (mode == "oom")
        {
            try { GC.KeepAlive(new byte[checked((int)identity.GcHeapLimitBytes + 1)]); return 18; }
            catch (OutOfMemoryException) { await Console.OpenStandardOutput().WriteAsync(MediaWorkerProtocol.Write(new(MediaWorkerStatus.LimitExceeded, identity))); return 0; }
        }
        if (mode is "managed-reject" or "managed-control")
        {
            const int requested = 536870913;
            bool rejected = false;
            try { GC.KeepAlive(new byte[requested]); }
            catch (OutOfMemoryException) { rejected = true; }
            await File.WriteAllTextAsync(arguments[1] + ".managed-probe", $"requested={requested};largeRejected={rejected};gc={identity.GcHeapLimitBytes}");
            if (rejected != (mode == "managed-reject")) return 18;
            if (rejected)
            {
                await Console.OpenStandardOutput().WriteAsync(MediaWorkerProtocol.Write(new(MediaWorkerStatus.LimitExceeded, identity)));
                return 0;
            }
        }
        if (mode is "native-memory" or "native-control")
        {
            if (!OperatingSystem.IsLinux())
            {
                await Console.OpenStandardOutput().WriteAsync(MediaWorkerProtocol.Write(new(MediaWorkerStatus.Unsupported, identity)));
                return 0;
            }
            nint small = Marshal.AllocHGlobal(65536);
            try { for (int index = 0; index < 65536; index++) Marshal.WriteByte(small, index, 0x5a); }
            finally { Marshal.FreeHGlobal(small); }
            bool rejected = false;
            nint oversized = 0;
            const long requested = (1L << 30) + (64L << 20);
            try { oversized = Marshal.AllocHGlobal(checked((nint)requested)); }
            catch (OutOfMemoryException) { rejected = true; }
            finally { if (oversized != 0) Marshal.FreeHGlobal(oversized); }
            // Never touch an oversized allocation if the OS accepted it.
            await File.WriteAllTextAsync(arguments[1] + ".native-probe", $"smallTouched=65536;requested={requested};largeRejected={rejected};data={identity.LinuxLimits!.DataBytes}");
            if (rejected != (mode == "native-memory")) return 18;
        }
        if (mode == "environment")
        {
            string[] safe = ["DOTNET_GCHeapHardLimit", "DOTNET_gcServer", "DOTNET_PROCESSOR_COUNT", "DOTNET_EnableDiagnostics"];
            await File.WriteAllTextAsync(arguments[1] + ".environment-names", string.Join(",", Environment.GetEnvironmentVariables().Keys.Cast<string>().Order(StringComparer.Ordinal)));
            // CoreFoundation inserts this key inside the macOS child; the
            // supervisor's actual launch environment remains the four knobs.
            if (Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(key => !safe.Contains(key, StringComparer.Ordinal) &&
                !(OperatingSystem.IsMacOS() && key == "__CF_USER_TEXT_ENCODING"))) return 19;
        }
        if (mode is "encode-wrong-source" or "encode-wrong-length" or "encode-wrong-output" or "encode-wrong-build" or "encode-cross-kind-build" or "encode-normal-priority" or "encode-truncated" or "encode-success-nonzero")
        {
            if (!NativePriority.TryReduce(out string priorityKind, out int priorityValue)) return 64;
            var actual = await MediaEncodingService.EncodeAsync(arguments[1], arguments[2],
                long.Parse(arguments[3], CultureInfo.InvariantCulture), long.Parse(arguments[4], CultureInfo.InvariantCulture),
                long.Parse(arguments[5], CultureInfo.InvariantCulture), long.Parse(arguments[6], CultureInfo.InvariantCulture),
                arguments[7] == "gamecube" ? Romd.Media.Nintendo.NintendoDiscKind.GameCube : Romd.Media.Nintendo.NintendoDiscKind.Wii,
                priorityKind, priorityValue, CancellationToken.None);
            var facts = actual.Encoding!;
            if (mode == "encode-wrong-source") facts = facts with { SourceSha256 = new string('a', 64) };
            if (mode == "encode-wrong-length") facts = facts with { LogicalBytes = facts.LogicalBytes + 1 };
            if (mode == "encode-wrong-output") facts = facts with { ArtifactBytes = long.Parse(arguments[6], CultureInfo.InvariantCulture) + 1 };
            if (mode == "encode-wrong-build") facts = facts with { EncoderBuild = "invented" };
            if (mode == "encode-cross-kind-build") facts = facts with { EncoderBuild = arguments[7] == "wii" ?
                Romd.Media.Rvz.RvzEncoder.EncoderBuild : Romd.Media.Rvz.RvzWiiEncoder.EncoderBuild };
            if (mode == "encode-normal-priority") facts = facts with { PriorityValue = 0 };
            byte[] result = MediaWorkerProtocol.Write(new(MediaWorkerStatus.Encoded, identity, Encoding: facts));
            await Console.OpenStandardOutput().WriteAsync(mode == "encode-truncated" ? result[..^2] : result);
            return mode == "encode-success-nonzero" ? 17 : 0;
        }
        if (mode is "iso-wrong-artifact" or "iso-wrong-hash" or "iso-wrong-length" or "iso-invalid-counts" or "iso-missing-facts" or "iso-reply-for-rvz" or "rvz-reply-for-iso" or "ps2-reply-for-rvz")
        {
            var actual = mode == "rvz-reply-for-iso"
                ? await MediaInspectionService.InspectRvzAsync(arguments[1], 16L << 20, 16L << 20, CancellationToken.None)
                : await MediaInspectionService.InspectIsoAsync(arguments[1], 16L << 20, 16L << 20, CancellationToken.None);
            var facts = actual.Inspection!;
            var iso = actual.IsoInspection;
            if (mode == "iso-wrong-artifact") facts = facts with { ArtifactSha256 = new string('a', 64), LogicalSha256 = new string('a', 64) };
            if (mode == "iso-wrong-hash") facts = facts with { LogicalSha256 = new string('a', 64) };
            if (mode == "iso-wrong-length") facts = facts with { LogicalBytes = facts.LogicalBytes + 1 };
            if (mode == "iso-invalid-counts") iso = iso! with { Layout = iso!.Layout! with { WiiPartitionCount = 1 } };
            if (mode == "iso-missing-facts") iso = null;
            await Console.OpenStandardOutput().WriteAsync(MediaWorkerProtocol.Write(new(MediaWorkerStatus.Inspected, identity, facts, iso,
                Ps2Inspection: actual.Ps2Inspection)));
            return 0;
        }
        if (mode is "truncated-output" or "success-nonzero" or "wrong-artifact" or "wrong-profile" or "oversized-logical")
        {
            var actual = await MediaInspectionService.InspectRvzAsync(arguments[1], maxArtifactBytes: 16L << 20, maxLogicalBytes: 16L << 20, CancellationToken.None);
            var facts = actual.Inspection!;
            if (mode == "wrong-artifact") facts = facts with { ArtifactSha256 = new string('a', 64) };
            if (mode == "wrong-profile") facts = facts with { EncodingProfile = "rvz-import/v1;codec=5;level=19;block=65537;format=01000000;encoder=unknown" };
            if (mode == "oversized-logical") facts = facts with { LogicalBytes = 16L << 30 };
            byte[] result = MediaWorkerProtocol.Write(new(MediaWorkerStatus.Inspected, identity, facts));
            await Console.OpenStandardOutput().WriteAsync(mode == "truncated-output" ? result[..^2] : result);
            return mode == "success-nonzero" ? 17 : 0;
        }
        """;
}
