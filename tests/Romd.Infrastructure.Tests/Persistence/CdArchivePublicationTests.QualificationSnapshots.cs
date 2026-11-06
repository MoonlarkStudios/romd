using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class CdArchivePublicationTests
{
    [Fact]
    public async Task QualificationSnapshot_WholeLogicalIdentity_ExecutesActualHarnessQueryAgainstFreshBaseline()
    {
        await database.ProvisionAsync();
        using var snapshot = await QualificationSnapshotAsync("whole", Identity(originals["track.bin"]).Sha1.ToString());
        var facts = snapshot.RootElement;
        facts.GetProperty("schemaVersion").GetInt32().ShouldBe(PostgreSqlConfiguration.ExpectedSchemaVersion);
        facts.GetProperty("logicalRoms").GetArrayLength().ShouldBe(1);
        facts.GetProperty("logicalRoms")[0].GetProperty("Id").GetInt32().ShouldBe(1);
        var output = facts.GetProperty("outputs").EnumerateArray().ShouldHaveSingleItem();
        output.GetProperty("RomId").GetInt32().ShouldBe(1);
        facts.GetProperty("logicalRoms")[0].GetProperty("PreferredOutputId").GetInt32().ShouldBe(output.GetProperty("Id").GetInt32());
        facts.GetProperty("representations").GetArrayLength().ShouldBe(1);
        facts.GetProperty("files").EnumerateArray().ShouldHaveSingleItem().GetProperty("Id").GetInt32().ShouldBe(1);
        facts.GetProperty("verifications").GetArrayLength().ShouldBe(1);
        facts.GetProperty("verificationOutputs").EnumerateArray().ShouldHaveSingleItem().GetProperty("OutputId").GetInt32().ShouldBe(output.GetProperty("Id").GetInt32());
        facts.GetProperty("manifests").GetArrayLength().ShouldBe(0);
        facts.GetProperty("sourcePins").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task QualificationSnapshot_CdPublication_RetainsNormalizedOutputsDescriptorsCreationAndOwnership()
    {
        await database.ProvisionAsync();
        await Publish();
        using var snapshot = await QualificationSnapshotAsync("cd");
        var facts = snapshot.RootElement;
        var representation = facts.GetProperty("representations").EnumerateArray().ShouldHaveSingleItem();
        int id = representation.GetProperty("Id").GetInt32();
        representation.GetProperty("Encoding").GetString().ShouldBe("Chd");
        facts.GetProperty("logicalRoms").GetArrayLength().ShouldBe(2);
        var outputs = facts.GetProperty("outputs").EnumerateArray().ToArray();
        outputs.Length.ShouldBe(2);
        outputs.Select(row => row.GetProperty("MemberOrdinal").GetInt32()).Order().ShouldBe([0, 1]);
        outputs.ShouldAllBe(row => row.GetProperty("RepresentationId").GetInt32() == id);
        facts.GetProperty("members").GetArrayLength().ShouldBe(3);
        var cue = facts.GetProperty("members").EnumerateArray().Single(row => row.GetProperty("StoredRole").GetString() == "Cue");
        cue.GetProperty("StoredFileId").GetInt32().ShouldBe(3);
        cue.GetProperty("ContentSha256").GetString().ShouldBe("\\x" + Identity(originals["disc.cue"]).Sha256);
        facts.GetProperty("files").EnumerateArray().Select(row => row.GetProperty("Id").GetInt32()).Order().ShouldBe([3, 9]);
        facts.GetProperty("tracks").GetArrayLength().ShouldBe(2);
        facts.GetProperty("memberTracks").GetArrayLength().ShouldBe(2);
        facts.GetProperty("verifications").GetArrayLength().ShouldBe(1);
        facts.GetProperty("verificationOutputs").GetArrayLength().ShouldBe(2);
        facts.GetProperty("creations").GetArrayLength().ShouldBe(1);
        facts.GetProperty("creationSources").EnumerateArray().Select(row => row.GetProperty("TargetOutputId").GetInt32()).Order()
            .ShouldBe(outputs.Select(row => row.GetProperty("Id").GetInt32()).Order());
        facts.GetProperty("archiveOwners").EnumerateArray().ShouldHaveSingleItem().GetProperty("RepresentationId").GetInt32().ShouldBe(id);
        facts.GetProperty("cdBootInspections").GetArrayLength().ShouldBe(1);
        facts.GetProperty("cdEncodingObservations").GetArrayLength().ShouldBe(1);
        facts.GetProperty("encodingAttempts").GetArrayLength().ShouldBe(1);
        facts.GetProperty("sourcePins").GetInt32().ShouldBe(0);
        facts.GetProperty("pendingDemand").GetInt32().ShouldBe(0);
        string root = QualificationRepositoryRoot();
        var start = new ProcessStartInfo("python3")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import json,sys; from types import SimpleNamespace; sys.path.insert(0,sys.argv[1]); from chd_cd import CdQualification; value=json.load(sys.stdin); run=object.__new__(CdQualification); run.settings=SimpleNamespace(recipe=sys.argv[2]); run.record={}; run.identities=value['identities']; run.capture_archive(value['facts']); print(json.dumps(run.cd_artifact))");
        start.ArgumentList.Add(Path.Combine(root, "scripts", "e2e"));
        start.ArgumentList.Add(Recipe.Id);
        using var process = Process.Start(start).ShouldNotBeNull();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var identities = originals.ToDictionary(pair => pair.Key, pair =>
        {
            var identity = Identity(pair.Value);
            return new { size = identity.Size, sha256 = identity.Sha256.ToString(), sha1 = identity.Sha1.ToString(), md5 = identity.Md5.ToString(), crc32 = identity.Crc32.ToString() };
        });
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { facts, identities }));
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        process.ExitCode.ShouldBe(0, await errorTask);
        using var artifact = JsonDocument.Parse(await outputTask);
        artifact.RootElement.GetProperty("size").GetInt64().ShouldBe(Measured().Measurement.ArtifactSize);
        artifact.RootElement.GetProperty("sha256").GetString().ShouldBe(Measured().Measurement.ArtifactSha256.ToString());
    }

    private async Task<JsonDocument> QualificationSnapshotAsync(string scope, string? sha1 = null)
    {
        string root = QualificationRepositoryRoot();
        // Import the actual Python query builder with structured arguments: no copied SQL or shell expansion in this fixture.
        var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import sys; sys.path.insert(0,sys.argv[1]); import media_facts; sys.stdout.write(media_facts.cd_snapshot_query() if sys.argv[2]=='cd' else media_facts.whole_snapshot_query(sys.argv[3]))");
        start.ArgumentList.Add(Path.Combine(root, "scripts", "e2e"));
        start.ArgumentList.Add(scope);
        if (sha1 is not null)
            start.ArgumentList.Add(sha1);
        using var process = Process.Start(start).ShouldNotBeNull();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.ShouldBe(0, await error);
        string query = await output;
        await using var db = database.CreateContext();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = query;
        return JsonDocument.Parse(((string?)(await command.ExecuteScalarAsync())).ShouldNotBeNull());
    }
    private static string QualificationRepositoryRoot()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, ".mise.toml")))
            root = Path.GetDirectoryName(root);
        return root.ShouldNotBeNull();
    }

}
