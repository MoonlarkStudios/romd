using System.Text.Json;
using Romd.Media.PlayStation;
using Romd.Media.Processes;
using Romd.Infrastructure.Storage;

namespace Romd.Media.Probe;

/// <summary>Read-only managed PS1 admission diagnostics on independently DAT-matched original sets.</summary>
internal static class ChdCdBootChecks
{
    internal static async Task RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 3)
            throw new ArgumentException("Usage: --ps1-cd-identification-probe <absolute configuration.json> <absolute new report.json>");
        MediaProbePaths.Validate([args[1], args[2]]);
        var config = ChdCdQualificationInputs.Parse(BoundedRuntimeFile.Read(args[1], 1 << 20));
        ChdCdQualificationInputs.ValidatePaths(config, args[1], args[2]);
        List<object> rows = [];
        await using var report = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            foreach (var input in config.Inputs)
            {
                var layout = ChdCdQualificationInputs.ReadLayout(input);
                foreach (var original in layout.Dat)
                {
                    var hashes = await Digests.FileAsync(Path.Combine(input.FixtureDirectory, original.Key), ct);
                    ChdCdQualificationInputs.MatchDat(hashes, original.Value);
                }
                using var first = new ChdCdRawFile(Path.Combine(input.FixtureDirectory, layout.Cue.Files[0].Name));
                var observed = Ps1CdIdentifier.Inspect(first, layout.Cue.TrackTypes[0], layout.Cue.FirstTrackIndex01,
                    layout.Cue.Files.Sum(file => new FileInfo(Path.Combine(input.FixtureDirectory, file.Name)).Length / 2352), ct);
                Check.That(observed is not null && Ps1CdIdentifier.Valid(observed), "bounded PS1 root executable in exact original BIN");
                rows.Add(new { input.Name, input.DatSha256, input.DatEntryName, layout.DatNames, layout.CatalogCueReference, Boot = observed });
            }
            await Save("passed", null);
        }
        catch (Exception error) { await Save("failed", error.ToString()); throw; }
        async Task Save(string status, string? error) => await JsonSerializer.SerializeAsync(report, new
        {
            Status = status,
            Profile = "ps1-cd-boot-qualification/v1",
            config.SourceDescription,
            config.FixtureKind,
            Rows = rows,
            Error = error,
            Scope = "Bounded managed recognition on DAT-matched original BINs. Production repeats this in supervised encoder and verifier children. No encoding, deployment or gameplay qualification."
        }, JsonOptions.Value, CancellationToken.None);
    }
}
