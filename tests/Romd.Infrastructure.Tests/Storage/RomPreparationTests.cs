using Romd.Media.Inspection;
using System.Security.Cryptography;
using NSubstitute;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Hashing;
using Romd.Infrastructure.Storage;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RomPreparationTests
{
    [Theory]
    [InlineData(false, "game.nes", true)]
    [InlineData(true, "disc.bin", false)]
    [InlineData(false, "DISC.RVZ", false)]
    public void StagingHashes_AdapterDecidesWithoutResolvingVerifier(bool rvzMagic, string filename, bool expected)
    {
        var service = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => throw new InvalidOperationException("Verifier must remain lazy"), new MediaInfrastructureTestServices.EmptyReferences());
        service.RequiresDatHashes(rvzMagic ? "RVZ\u0001"u8.ToArray() : new byte[] { 1, 2, 3 }, filename).ShouldBe(expected);
    }

    [Fact]
    public async Task PrepareAsync_OrdinaryFile_PreservesExactArtifactAndLogicalIdentity()
    {
        await using var source = new Temp([1, 2, 3]);
        var service = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => new RvzVerifier(new()), new MediaInfrastructureTestServices.EmptyReferences());
        await using (var prepared = (await service.PrepareAsync(source, "game.bin")).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>())
        {
            prepared.Encoding.ShouldBe(RomRepresentationEncoding.Identity);
            prepared.Artifact.ShouldBeSameAs(source);
            prepared.ContentSha256.ShouldBe(source.FileSha256);
            prepared.Hashes.BytesRead.ShouldBe(3);
            prepared.Hashes.Sha1.ShouldBe(Sha1.FromSpan(SHA1.HashData(new byte[] { 1, 2, 3 })));
            prepared.Verification.ContentSha256.ShouldBe(source.FileSha256);
            prepared.Verification.ArtifactSha256.ShouldBe(source.FileSha256);
            prepared.Verification.DecoderBuild.ShouldBe("identity/v1");
            prepared.Verification.VerifierBuild.ShouldStartWith("romd-media/mvid:");
        }
        File.Exists(source.Path).ShouldBeTrue();
    }

    [Fact]
    public async Task PrepareAsync_FalseRvzExtension_RejectsBeforePublication()
    {
        await using var source = new Temp([1, 2, 3, 4]);
        var service = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => new RvzVerifier(new()), new MediaInfrastructureTestServices.EmptyReferences());
        (await service.PrepareAsync(source, "disc.rvz")).IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task PrepareAsync_InvalidMediaOptions_OrdinaryContentDoesNotResolveVerifier()
    {
        await using var source = new Temp([1, 2, 3]);
        var service = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(),
            () => new RvzVerifier(new() { MaxConcurrent = 0 }), new MediaInfrastructureTestServices.EmptyReferences());
        await using var prepared = (await service.PrepareAsync(source, "game.nes")).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();
        prepared.Encoding.ShouldBe(RomRepresentationEncoding.Identity);
        prepared.EncodingProfile.ShouldBe("identity/v1");
    }

    [Theory]
    [InlineData(0u, 0, 32768u)]
    [InlineData(5u, 19, 131072u)]
    [InlineData(3u, -1, 4194304u)]
    public void RvzProfile_CapturesContainerSettingsWithoutInventingEncoder(uint codec, int level, uint block)
    {
        RvzInspectionProfile.Capture(new(0x01000000, 0, codec, level, (int)block, false)).ShouldBe($"rvz-import/v1;codec={codec};level={level};block={block};format=01000000;encoder=unknown");
    }

    [Theory]
    [InlineData(1u, 32768u)]
    [InlineData(5u, 16384u)]
    [InlineData(5u, 65537u)]
    public void RvzProfile_UnsupportedCodecOrBlock_IsRejected(uint codec, uint block)
    {
        RvzInspectionProfile.IsValid(RvzInspectionProfile.Capture(new(0x01000000, 0, codec, 19, (int)block, false))).ShouldBeFalse();
    }

    [Fact]
    public async Task PrepareAsync_StagedDatHashes_ReusesMeasurementWithoutHashing()
    {
        byte[] bytes = [1, 2, 3];
        using var input = new MemoryStream(bytes);
        var measured = await new HashingService().ComputeDatHashesAsync(input);
        await using var source = new Temp(bytes, measured);
        var hashing = Substitute.For<IHashingService>();
        var service = MediaInfrastructureTestServices.ManagedPreparation(hashing, () => new RvzVerifier(new() { MaxConcurrent = 0 }), new MediaInfrastructureTestServices.EmptyReferences());

        await using var prepared = (await service.PrepareAsync(source, "game.bin")).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();

        prepared.Hashes.ShouldBeSameAs(measured);
        prepared.Verification.Sha1.ShouldBe(measured.Sha1);
        prepared.Verification.Md5.ShouldBe(measured.Md5);
        prepared.Verification.Crc32.ShouldBe(measured.Crc32);
        hashing.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmitAsync_OrdinaryOrInvalidPrefix_DoesNotResolveVerifier(bool rvzExtension)
    {
        int resolutions = 0;
        var service = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () =>
        {
            resolutions++;
            return new RvzVerifier(new() { MaxConcurrent = 0 });
        }, new MediaInfrastructureTestServices.EmptyReferences());

        if (rvzExtension)
            await Should.ThrowAsync<InvalidDataException>(() => service.AdmitAsync(new byte[] { 1, 2, 3, 4 }, "disc.rvz"));
        else
            (await service.AdmitAsync(new byte[] { 1, 2, 3, 4 }, "game.bin")).ShouldBeNull();

        resolutions.ShouldBe(0);
    }

    [Fact]
    public async Task PrepareAsync_Rvz_UsesLogicalHashesAndContainerProfile()
    {
        var disc = new SyntheticRvzBuilder(0x100000, 0x20000).Build();
        await using var source = new Temp(disc.File);
        using var verifier = new RvzVerifier(new());
        var service = MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => verifier, new MediaInfrastructureTestServices.EmptyReferences());

        await using var prepared = (await service.PrepareAsync(source, "disc.rvz")).Value.ShouldBeOfType<Romd.Admin.Application.Source.Rom.PreparedRom.WholeFile>();

        prepared.Artifact.ShouldBeSameAs(source);
        prepared.LogicalFilename.ShouldBe("disc.iso");
        prepared.Encoding.ShouldBe(RomRepresentationEncoding.Rvz);
        RvzInspectionProfile.IsValid(prepared.EncodingProfile).ShouldBeTrue();
        prepared.EncodingProfile.ShouldContain("encoder=unknown");
        prepared.ContentSha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(disc.ExpectedDisc)));
        prepared.Hashes.BytesRead.ShouldBe(disc.ExpectedDisc.Length);
        prepared.Hashes.Sha1.ShouldBe(Sha1.FromSpan(SHA1.HashData(disc.ExpectedDisc)));
        prepared.Hashes.Md5.ShouldBe(Md5.FromSpan(MD5.HashData(disc.ExpectedDisc)));
        prepared.Verification.ArtifactSha256.ShouldBe(source.FileSha256);
        prepared.Verification.ArtifactSize.ShouldBe(source.FileSize);
        prepared.Verification.Profile.ShouldBe(RvzVerifier.Profile);
    }

    private sealed class Temp : ITempFile
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

        public Temp(byte[] bytes, DatHashResult? hashes = null)
        {
            Path = System.IO.Path.GetTempFileName();
            File.WriteAllBytes(Path, bytes);
            FileSize = bytes.Length;
            FileSha256 = Sha256.FromSpan(SHA256.HashData(bytes));
            DatHashes = hashes;
        }
        public string Path { get; }
        public Sha256 FileSha256 { get; }
        public long FileSize { get; }
        public DatHashResult? DatHashes { get; }
        public Stream OpenRead() => File.OpenRead(Path);
        public ValueTask DisposeAsync() { if (!_quarantined) File.Delete(Path); return ValueTask.CompletedTask; }
    }
}
