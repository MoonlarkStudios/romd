using System.Text;
using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Source.Rom;
using Romd.Application.Common.Content;
using Romd.Infrastructure.Storage;
using Romd.Media.TestSupport;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed partial class MediaReconciliationEncodingTests
{
    [LinuxEncodingTheory]
    [InlineData("valid")]
    [InlineData("cd-backed")]
    [InlineData("changed-source")]
    [InlineData("unknown-stop")]
    [InlineData("stopped-cancel")]
    [InlineData("deadline")]
    public async Task Adopt_CanonicalRetainedArchive_ComparesAuthenticOriginalsWithoutResolvingAnEncoder(string outcome)
    {
        await using var f = CreateFixture(true, null, null);
        var bin = f.Original(SyntheticPs1Cd.ToRaw(SyntheticPs1Cd.CodecImage()), "data.bin", MediaReconciliationSourceRole.Bin, 1);
        var cue = f.Stored(Encoding.UTF8.GetBytes("FILE \"data.bin\" BINARY\n TRACK 01 MODE1/2352\n INDEX 01 00:00:00\n"), "disc.cue", MediaReconciliationSourceRole.Cue, 2);
        var sidecar = f.Stored([0x53, 0x42, 0x49, 0, 0xfe], "disc.sbi", MediaReconciliationSourceRole.Sidecar, 3);
        var original = new MediaReconciliationLayout.Original(42, Guid.NewGuid(), 1, 1, "fixture-catalog", "disc.cue");
        var originals = new MediaReconciliationSources([bin, cue, sidecar], original, [], null);
        // Produce public synthetic retained bytes once as fixture preparation. Adoption below has no encoding capability.
        var encoding = await f.Encoding(f.Profile).AcquireAsync(f.Attempt("ps1-cd-set", originals));
        encoding.IsError.ShouldBeFalse();
        var encoded = await encoding.Value.EncodeAndVerifyAsync(_ => Task.CompletedTask);
        encoded.IsError.ShouldBeFalse(encoded.IsError ? encoded.FirstError.ToString() : null);
        var archive = encoded.Value.ShouldBeOfType<MediaReconciliationEncoded.Cd>();
        var measured = archive.Measured;
        byte[] retained = await File.ReadAllBytesAsync(archive.Artifact.Path);
        f.Payloads.Add(50, retained);
        var graph = new RomRepresentationFacts(50, 50, archive.Artifact.FileSha256, archive.Artifact.FileSize, measured.Container, measured.Manifest,
            measured.Measurement.Outputs.Select((output, index) => new RomRepresentationOutput(500 + index, bin.Output.RomId, output.Selector, output.Identity)).ToArray(),
            [measured.Measurement], ((RomReconstructionManifest.CdTracks)measured.Manifest).Members.Select((member, index) => (member, index))
                .Where(pair => pair.member is RomCdStoredMember).Select(pair => new RomRetainedDescriptor(pair.index,
                    pair.member.Path == cue.Path ? cue.FileId : sidecar.FileId, pair.member.Identity.Sha256, pair.member.Identity.Size)).ToArray(), [], []);
        (await encoding.Value.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
        await encoding.Value.DisposeAsync();

        if (outcome == "cd-backed")
        {
            bin = f.CdSource(graph, retained, "data.bin");
            bin.Graph.Id.ShouldBe(graph.Id);
            bin.Output.Selector.ShouldBeOfType<RomOutputSelector.Member>();
        }
        f.Capabilities.CanEncode(Arg.Any<string>()).Returns(false);
        var profile = Substitute.For<IMediaReconciliationMediaProfile>();
        profile.LimitsFingerprint.Returns(f.Profile.LimitsFingerprint);
        profile.Encoder(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("Adoption must not resolve an encoder."));
        var witness = new MediaReconciliationLayout.Catalog(7, Guid.NewGuid(), original.PlatformId,
            new(13, original.ReleaseId, original.CatalogFingerprint, "cue-fingerprint", cue.Path!, cue.Identity.Size, cue.Identity.Sha1, cue.Identity.Md5, cue.Identity.Crc32), "fixture-formatter");
        var sources = new MediaReconciliationSources([bin, cue, sidecar, new MediaReconciliationInput.Artifact(graph, measured.Measurement.Id, witness)], original, [], null);
        var attempt = new MediaReconciliationAttempt(1, new(Guid.NewGuid(), 1, sources, MediaReconciliationOperation.Adopt,
            f.Composition.Reconciliation["ps1-cd-set"].Recipe, Hash('c'), profile.LimitsFingerprint, measured.Measurement.Decoder, "ps1-cd-set", null, null), null);
        var injected = Substitute.For<IMediaWorkerSupervisor>();
        string[] held = [];
        injected.VerifyChdCdAsync(Arg.Any<ITempFile>(), Arg.Any<ITempFile>(), Arg.Any<IReadOnlyList<ITempFile>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            held = call.Arg<IReadOnlyList<ITempFile>>().Select(file => file.Path).Append(call.ArgAt<ITempFile>(0).Path).Append(call.ArgAt<ITempFile>(1).Path).ToArray();
            return outcome switch
            {
                "deadline" => Task.FromResult(new MediaWorkerResult(null, MediaWorkerFailure.CpuDeadline, null, ProcessTreeStopped: true)),
                "stopped-cancel" => Task.FromException<MediaWorkerResult>(new MediaWorkerStoppedCancellationException(CancellationToken.None)),
                _ => Task.FromException<MediaWorkerResult>(new IOException("uncertified comparison stop"))
            };
        });
        if (outcome == "changed-source")
            f.Payloads[50] = new byte[retained.Length];
        var acquired = await f.Adoption(profile, outcome is "valid" or "cd-backed" ? null : injected).AcquireAsync(attempt);
        acquired.IsError.ShouldBeFalse(acquired.IsError ? acquired.FirstError.ToString() : null);
        var lease = acquired.Value;
        if (outcome == "stopped-cancel")
            await Should.ThrowAsync<OperationCanceledException>(() => lease.CompareOriginalsAsync());
        else
        {
            var result = await lease.CompareOriginalsAsync();
            if (outcome is "valid" or "cd-backed")
            {
                result.IsError.ShouldBeFalse(result.IsError ? result.FirstError.ToString() : null);
                result.Value.Artifact.FileSha256.ShouldBe(graph.ArtifactSha256);
                result.Value.Measured.MatchesRetained(graph).ShouldBeTrue();
                result.Value.Measured.Measurement.Id.ShouldNotBe(measured.Measurement.Id);
                result.Value.Measured.Measurement.Decoder.ShouldBe(attempt.Capture.Decoder);
                result.Value.Measurement.Boot.ShouldNotBeNull();
                result.Value.StoredOriginals.Keys.ShouldBe(["disc.cue", "disc.sbi"], ignoreOrder: true);
                await Should.ThrowAsync<InvalidOperationException>(() => lease.CompareOriginalsAsync());
            }
            else if (outcome == "unknown-stop")
                result.FirstError.ShouldBe(RomMediaVerificationErrors.Custody);
            else if (outcome == "deadline")
                result.FirstError.Code.ShouldBe("Rom.Media.CpuDeadline");
            else
            { result.FirstError.ShouldBe(MediaReconciliationErrors.Measurement); injected.ReceivedCalls().ShouldBeEmpty(); }
        }
        profile.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(IMediaReconciliationMediaProfile.Encoder)).ShouldBeFalse();
        if (outcome == "unknown-stop")
        {
            lease.ProcessTreeStopped.ShouldBeFalse();
            (await lease.ReleaseProcessingAsync()).FirstError.ShouldBe(RomMediaVerificationErrors.Custody);
            await lease.DisposeAsync();
            held.Length.ShouldBe(6);
            held.ShouldAllBe(path => File.Exists(path + ".quarantine"));
            await Should.ThrowAsync<ResourceCapacityException>(() => f.Runtime.AcquireAsync());
        }
        else
        {
            lease.ProcessTreeStopped.ShouldBeTrue();
            (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
            await lease.DisposeAsync();
            using var capacity = await f.Runtime.AcquireAsync();
            Directory.GetFiles(f.Root, "*.quarantine", SearchOption.AllDirectories).ShouldBeEmpty();
        }
        await f.Files.DidNotReceiveWithAnyArgs().StageRawAsync(default!, default);
    }
}
