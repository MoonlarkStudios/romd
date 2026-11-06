using System.Text;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Polly.Registry;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.IngestRom;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Jobs.Processors;
using Romd.Infrastructure.Resilience;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed partial class RomProcessorTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(2, false)]
    public async Task ProcessAsync_CdReceipt_PreservesOutcomeAndCatalogScopeWithoutChoosingABinRom(int binCount, bool isNew)
    {
        var native = new RomChdDecoderBuild("linux-x64", Sha1.Parse(new string('a', 40)), 1);
        var raw = Sha1.Parse(new string('b', 40));
        var overall = Sha1.Parse(new string('c', 40));
        var container = MediaFactFixtures.Chd(binCount * 4 * 2448, 19584, 2448, [0x63646c7a, 0, 0, 0], raw, overall,
            Enumerable.Range(1, binCount).Select(number => new RomChdMetadataEntry(0x43485432, 1,
                Encoding.ASCII.GetBytes($"TRACK:{number} TYPE:MODE2_RAW SUBTYPE:NONE FRAMES:4"))).ToArray());
        var identities = Enumerable.Range(1, binCount).Select(number => MediaFactFixtures.Identity(Enumerable.Repeat((byte)number, 4 * 2352).ToArray())).ToArray();
        var cue = MediaFactFixtures.Identity(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, binCount)
            .Select(number => $"FILE \"track{number}.bin\" BINARY\r\n  TRACK {number:00} MODE2/2352\r\n    INDEX 01 00:00:00\r\n"))));
        RomCdFileIdentity FileIdentity(RomLogicalIdentity identity) => new(identity.Size, identity.Sha256, identity.Sha1, identity.Md5, identity.Crc32);
        var manifest = new RomReconstructionManifest.CdTracks(binCount * 4,
            Enumerable.Range(1, binCount).Select(number => new RomCdTrack(number, (number - 1) * 4, 4, RomCdTrackKind.Mode2Raw)).ToArray(),
            identities.Select((identity, index) => (RomCdFileMember)new RomCdBinMember($"track{index + 1}.bin", FileIdentity(identity), FileIdentity(identity), [index + 1]))
                .Append(new RomCdStoredMember("disc.cue", FileIdentity(cue), RomCdStoredRole.Cue)).ToArray());
        var artifact = Sha256.Parse(new string('d', 64));
        var proof = new RomRepresentationVerification(Guid.NewGuid(), artifact, 512, new RomDecoderIdentity.Native(native), "chd-cd-inspection/v2",
            "worker-fixture", DateTimeOffset.UnixEpoch, identities.Select((identity, index) => new RomMeasuredOutput(new RomOutputSelector.Member(index), identity)).ToArray(), container, raw, overall);
        var archive = new RomRepresentationFacts(41, 43, artifact, 512, container, manifest,
            identities.Select((identity, index) => new RomRepresentationOutput(100 + index, 200 + index, new RomOutputSelector.Member(index), identity)).ToArray(),
            [proof], [new(binCount, 49, cue.Sha256, cue.Size)]);
        var receipt = new RomIngestResult.CdArchive(archive, originalCue: null) { IsNew = isNew, MatchedTitleIds = [7], PlatformId = 3 };
        var handler = Substitute.For<ICommandHandler<IngestRomCommand, RomIngestResult>>();
        handler.HandleAsync(Arg.Any<IngestRomCommand>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<ErrorOr<RomIngestResult>>(receipt));
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        services.AddResiliencePolicies();
        await using var provider = services.BuildServiceProvider();
        var processor = new RomProcessor(provider, provider.GetRequiredService<ResiliencePipelineProvider<string>>(), NullLogger<RomProcessor>.Instance);
        string path = Path.Combine(Path.GetTempPath(), $"romd-cd-receipt-{Guid.NewGuid():N}.chd");
        try
        {
            await File.WriteAllBytesAsync(path, [1]);
            var result = await processor.ProcessAsync(path, allowUnidentified: false, archiveOnly: true, CancellationToken.None);

            result.Outcome.ShouldBe(isNew ? RomIngestOutcome.Ingested : RomIngestOutcome.Deduplicated);
            result.RomFileId.ShouldBeNull();
            result.MatchedTitleIds.ShouldBe([7]);
            result.PlatformId.ShouldBe(3);
            result.SizeBytes.ShouldBe(1);
        }
        finally { File.Delete(path); }
    }
}
