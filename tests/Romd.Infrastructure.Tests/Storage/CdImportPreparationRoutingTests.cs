using System.Buffers.Binary;
using NSubstitute;
using Romd.Admin.Application.Source.Rom;
using Romd.Infrastructure.Hashing;
using Romd.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class CdImportPreparationRoutingTests
{
    [Theory]
    [InlineData("MCom", "disc.bin")]
    [InlineData("MComprHD", "disc.rvz")]
    [InlineData("data", "disc.chd")]
    public void SelectPrefixLength_ChdSignatureOrHint_CapturesTheCompleteV5Header(string prefix, string filename) =>
        RomPreparationDetection.SelectPrefixLength(System.Text.Encoding.ASCII.GetBytes(prefix), filename).ShouldBe(124);

    [Theory]
    [InlineData(2448u, true)]
    [InlineData(2048u, false)]
    public void StagingHashes_ObservedCdUnit_CapturesRawArtifactChecksumsWithoutResolvingInspection(uint unitBytes, bool expected)
    {
        byte[] prefix = new byte[124];
        "MComprHD"u8.CopyTo(prefix);
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(8), 124);
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(60), unitBytes);
        var registry = new RomPreparationAdapterRegistry(new HashingService(),
            () => throw new InvalidOperationException("Hash selection does not inspect RVZ."),
            () => throw new InvalidOperationException("Hash selection does not inspect originals."),
            () => throw new InvalidOperationException("Hash selection does not inspect CHD."),
            () => throw new NotSupportedException("This fixture has no raw CD inspection."));
        var references = Substitute.For<IRomDiscMediaReferenceStore>();
        var service = new RomPreparationService(registry, new RomPs2DvdEligibilityResolver(references));

        service.RequiresDatHashes(prefix, "renamed.rvz").ShouldBe(expected);

        references.ReceivedCalls().ShouldBeEmpty();
    }
}
