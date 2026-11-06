using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Polly.Registry;
using Romd.Admin.Application.Resilience;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.IngestRom;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Jobs.Processors;
using Romd.Infrastructure.Resilience;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed partial class RomProcessorTests
{
    [Theory]
    [InlineData(".iso")]
    [InlineData(".gcm")]
    [InlineData(".wii")]
    [InlineData(".rvz")]
    public async Task ProcessAsync_ForwardsFilenameAndIngestionIntentThroughTheCommand(string extension)
    {
        var handler = Substitute.For<ICommandHandler<IngestRomCommand, RomIngestResult>>();
        handler.HandleAsync(Arg.Any<IngestRomCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<RomIngestResult>>(
                Error.Validation("Library.UnidentifiedRom", "No matching DAT fixture.")));
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        services.AddResiliencePolicies();
        await using var provider = services.BuildServiceProvider();
        var processor = new RomProcessor(
            provider,
            provider.GetRequiredService<ResiliencePipelineProvider<string>>(),
            NullLogger<RomProcessor>.Instance);
        string path = Path.Combine(Path.GetTempPath(), $"romd-rom-processor-{Guid.NewGuid():N}{extension}");

        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);

            var result = await processor.ProcessAsync(path, allowUnidentified: false, archiveOnly: false, CancellationToken.None);

            result.Outcome.ShouldBe(Romd.Domain.Jobs.RomIngestOutcome.Rejected);
            var command = handler.ReceivedCalls().ShouldHaveSingleItem().GetArguments()[0]
                .ShouldBeOfType<IngestRomCommand>();
            command.OriginalFilename.ShouldBe(Path.GetFileName(path));
            command.AllowUnidentified.ShouldBeFalse();
            command.ArchiveOnly.ShouldBeFalse();
            command.TrackedOnly.ShouldBeFalse();
            command.UploadItem.ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_ForwardsArchiveOnlyAndAllowUnidentified(
        bool archiveOnly)
    {
        var handler = Substitute.For<ICommandHandler<IngestRomCommand, RomIngestResult>>();
        handler.HandleAsync(Arg.Any<IngestRomCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ErrorOr<RomIngestResult>>(new RomIngestResult.WholeFile
            {
                RomFile = RomFile.Rehydrate(
                    1,
                    "game.rom",
                    2,
                    1,
                    Sha1.Parse(new string('0', 40)),
                    Md5.Parse(new string('0', 32)),
                    Crc32.Parse(new string('0', 8)),
                    DateTimeOffset.UtcNow),
                IsNew = true,
                MatchedTitleIds = [7],
                PlatformId = 3
            }));
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        services.AddResiliencePolicies();
        await using var provider = services.BuildServiceProvider();
        var processor = new RomProcessor(
            provider,
            provider.GetRequiredService<ResiliencePipelineProvider<string>>(),
            NullLogger<RomProcessor>.Instance);
        string path = Path.Combine(Path.GetTempPath(), $"romd-rom-processor-{Guid.NewGuid():N}.rom");

        try
        {
            await File.WriteAllBytesAsync(path, [1]);

            var result = await processor.ProcessAsync(
                path, allowUnidentified: true, archiveOnly, CancellationToken.None, trackedOnly: true);

            result.Outcome.ShouldBe(Romd.Domain.Jobs.RomIngestOutcome.Ingested);
            var command = handler.ReceivedCalls().ShouldHaveSingleItem().GetArguments()[0]
                .ShouldBeOfType<IngestRomCommand>();
            command.AllowUnidentified.ShouldBeTrue();
            command.ArchiveOnly.ShouldBe(archiveOnly);
            command.TrackedOnly.ShouldBeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
