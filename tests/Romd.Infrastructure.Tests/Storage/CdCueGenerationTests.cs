using System.IO.Abstractions;
using System.Text;
using NSubstitute;
using Romd.Admin.Application.Source.Rom.Compressed;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.PlayStation;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class CdCueGenerationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("romd-cd-cue-generation-").FullName;

    [Fact]
    public async Task GenerateAsync_FullSourceStaging_UsesSeparateCueCustodyAndCarriesActualIdentity()
    {
        var storage = Substitute.For<IRomdOptions>();
        storage.DataDirectory.Returns(root);
        storage.MaxUploadBytes.Returns(1 << 20);
        var files = new LocalTempFileFactory(storage, new FileSystem(), new TempStorageOptions
        { MaximumFiles = 1, MaximumBytes = 1 << 20, MinimumFreeBytes = 0, AdmissionTimeoutSeconds = 1 });
        await using var source = await files.CreateAsync(new MemoryStream(new byte[] { 1, 2, 3 }));
        var tracks = Tracks();
        var result = await new RomCdCueGenerator(files).GenerateAsync(tracks, ["data.bin", "audio.bin", "extra.bin"]);

        result.IsError.ShouldBeFalse();
        await using var generated = result.Value;
        byte[] expected = Encoding.UTF8.GetBytes(string.Join("\r\n", new[]
        {
            "FILE \"data.bin\" BINARY", "  TRACK 01 MODE2/2352", "    INDEX 01 00:00:00",
            "FILE \"audio.bin\" BINARY", "  TRACK 02 AUDIO", "    INDEX 00 00:00:00", "    INDEX 01 00:00:04",
            "FILE \"extra.bin\" BINARY", "  TRACK 03 MODE1/2352", "    PREGAP 00:00:02", "    INDEX 01 00:00:00", "    POSTGAP 00:00:03", ""
        }));
        File.ReadAllBytes(generated.Artifact.Path).ShouldBe(expected);
        generated.Identity.ShouldBe(MediaFactFixtures.Identity(expected));
        generated.Artifact.DatHashes.ShouldBeNull();
        generated.GeneratorProfile.ShouldBe(ChdCdCueRegenerator.Profile);
        generated.Artifact.Path.ShouldEndWith(".cue");
        Path.GetFileName(Path.GetDirectoryName(generated.Artifact.Path)).ShouldBe("cd-cue-staging-v1");
        File.Exists(source.Path).ShouldBeTrue();
        await generated.DisposeAsync();
        File.Exists(generated.Artifact.Path).ShouldBeFalse();
        File.Exists(source.Path).ShouldBeTrue();
        await using var recovered = (await files.ReserveCdCueAsync(1)).Value;
    }

    [Theory]
    [InlineData("../outside.bin", "audio.bin", "extra.bin")]
    [InlineData("data.iso", "audio.bin", "extra.bin")]
    [InlineData("DATA.bin", "data.bin", "extra.bin")]
    [InlineData("data.bin", "audio.bin", null)]
    public async Task GenerateAsync_UnsupportedNamesOrIncompleteAssignment_RefusesBeforeStaging(string first, string second, string? third)
    {
        var files = Substitute.For<ITempFileFactory>();
        string[] names = third is null ? [first, second] : [first, second, third];
        var result = await new RomCdCueGenerator(files).GenerateAsync(Tracks(), names);

        result.FirstError.ShouldBe(RomCdImportErrors.UnsupportedLayout);
        files.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task GenerateAsync_AlreadyCancelled_PreservesExactCallerTokenWithoutStaging()
    {
        var files = Substitute.For<ITempFileFactory>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        OperationCanceledException? observed = null;
        try
        { await new RomCdCueGenerator(files).GenerateAsync(Tracks(), ["data.bin", "audio.bin", "extra.bin"], cancellation.Token); }
        catch (OperationCanceledException error) { observed = error; }

        observed.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        files.ReceivedCalls().ShouldBeEmpty();
    }

    private static IReadOnlyList<RomCdInspectedTrack> Tracks() =>
    [
        new(new(1, 0, 17, RomCdTrackKind.Mode2Raw), 3, 0, false, 0, MediaFactFixtures.Identity(new byte[17 * 2352])),
        new(new(2, 20, 13, RomCdTrackKind.Audio), 3, 4, true, 0, MediaFactFixtures.Identity(new byte[13 * 2352])),
        new(new(3, 36, 8, RomCdTrackKind.Mode1Raw), 0, 2, false, 3, MediaFactFixtures.Identity(new byte[8 * 2352]))
    ];

    public void Dispose() => Directory.Delete(root, true);
}
