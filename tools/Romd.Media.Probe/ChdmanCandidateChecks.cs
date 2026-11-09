using System.Text.Json;
using System.Text.Json.Serialization;
using Romd.Infrastructure.Storage;
using Romd.Media.Chd;
using Romd.Media.Inspection;

namespace Romd.Media.Probe;

/// <summary>Verifies a configured chdman build directory against its own record without executing it.</summary>
internal static class ChdmanCandidateChecks
{
    private sealed record Configuration(string Directory);

    public static async Task RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 3)
            throw new ArgumentException("Usage: --chdman-candidate-probe <absolute configuration.json> <absolute new report.json>");
        MediaProbePaths.Validate([args[1], args[2]]);
        byte[] bytes = BoundedRuntimeFile.Read(args[1], 16384);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        Check.That(MediaRunRecord.Unique(document.RootElement), "unique tool candidate configuration");
        var config = JsonSerializer.Deserialize<Configuration>(bytes, new JsonSerializerOptions
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true })
            ?? throw new InvalidDataException("Missing tool candidate configuration.");
        var candidate = ChdmanCandidate.FromBuildRecord(config.Directory);
        MediaProbePaths.Validate([candidate.BinaryPath, candidate.ManifestPath]);
        ChdmanToolIdentity recorded = candidate.ExpectedIdentity;
        await using var output = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var rows = new List<object>();
        try
        {
            Verify("verified-pair");
            Reject("wrong-binary-sha256", recorded with { BinarySha256 = new string('1', 64) },
                "ManifestIdentityMismatch", candidate.ManifestPath);
            Reject("wrong-source-pin", recorded with { MameSourceSha256 = new string('1', 64) },
                "ManifestIdentityMismatch", candidate.ManifestPath);
            Verify("restored-pair");
            await JsonSerializer.SerializeAsync(output, new { Status = "passed", Rows = rows }, cancellationToken: ct);
        }
        catch (Exception error)
        {
            await JsonSerializer.SerializeAsync(output, new { Status = "failed", Rows = rows, Error = error.ToString() },
                cancellationToken: CancellationToken.None);
            throw;
        }

        void Verify(string step)
        {
            ChdmanToolIdentity actual = candidate.Verify(ct);
            Check.That(candidate.Matches(actual), "complete recorded tool identity");
            rows.Add(new { Step = step, Identity = actual });
        }

        void Reject(string step, ChdmanToolIdentity expected, string cause, string path)
        {
            var rejecting = new ChdmanCandidate(config.Directory, expected);
            try
            { rejecting.Verify(ct); }
            catch (ChdmanCandidateException error)
            {
                Check.That(error.Status == MediaWorkerStatus.Malformed &&
                    error.Rejection.Stage == ChdRejectionStage.NativeVerification &&
                    error.Rejection.Cause == cause && error.Rejection.Path == path, "exact tool candidate rejection");
                rows.Add(new { Step = step, error.Status, error.Rejection });
                return;
            }
            throw new InvalidDataException("Tool candidate negative control was accepted.");
        }
    }
}
