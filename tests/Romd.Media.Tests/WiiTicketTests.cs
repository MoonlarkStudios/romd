using System.Buffers.Binary;
using Romd.Media.Nintendo;
using Romd.Media.Rvz;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests;

public sealed class WiiTicketTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TryDecrypt_FixedIndependentRetailAndKoreanVectors_SelectsAuthoritativeCommonKey(byte index)
    {
        var vector = WiiTicketVectors.Get(index);
        byte[] actual = new byte[16];
        var ticket = new WiiTicket();

        ticket.TryDecrypt(vector.Ticket, actual).ShouldBeNull();

        actual.AsSpan().SequenceEqual(vector.ExpectedTitleKey).ShouldBeTrue("fixed independent ticket title-key vector");
        vector.Ticket[0x1F1] = (byte)(1 - index);
        ticket.TryDecrypt(vector.Ticket, actual).ShouldBeNull();
        actual.AsSpan().SequenceEqual(vector.ExpectedTitleKey).ShouldBeFalse("changing the common-key index selects a different key");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TryDecrypt_ExplicitConfiguredIndexAndTitleId_DerivesExpectedKeyWithoutFallback(byte index)
    {
        byte[] standard = SyntheticKey(41), korean = SyntheticKey(47), title = SyntheticKey(53), id = new byte[8];
        new Random(59).NextBytes(id);
        byte[] ticket = SyntheticWiiIsoBuilder.CreateTicket(index == 0 ? standard : korean, index, id, title);
        byte[] decoded = new byte[16];

        new WiiTicket(standard, korean).TryDecrypt(ticket, decoded).ShouldBeNull();

        decoded.AsSpan().SequenceEqual(title).ShouldBeTrue("configured ticket index and title-ID IV");
        ticket[0x1DC] ^= 1;
        new WiiTicket(standard, korean).TryDecrypt(ticket, decoded).ShouldBeNull();
        decoded.AsSpan().SequenceEqual(title).ShouldBeFalse("the title-ID IV participates in derivation");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    public void TryDecrypt_UnknownIndex_IsExplicitUnsupportedAndClearsDestination(byte index)
    {
        byte[] ticket = SyntheticWiiIsoBuilder.CreateTicket(SyntheticKey(41), index, new byte[8], SyntheticKey(53));
        byte[] decoded = Enumerable.Repeat((byte)0xA5, 16).ToArray();

        var failure = new WiiTicket(SyntheticKey(41), SyntheticKey(47)).TryDecrypt(ticket, decoded);

        failure.ShouldNotBeNull().Kind.ShouldBe(RvzEncodeFailureKind.UnsupportedMedia);
        decoded.AsSpan().IndexOfAnyExcept((byte)0).ShouldBe(-1);
    }

    [Fact]
    public void Validate_TruncatedOrOtherSignature_ReturnsTypedFailure()
    {
        WiiTicket.Validate(new byte[0x2A3]).ShouldNotBeNull().Kind.ShouldBe(RvzEncodeFailureKind.InvalidSource);
        byte[] ticket = new byte[0x2A4];
        BinaryPrimitives.WriteUInt32BigEndian(ticket, 0x00010000);
        WiiTicket.Validate(ticket).ShouldNotBeNull().Kind.ShouldBe(RvzEncodeFailureKind.UnsupportedMedia);
    }

    internal static byte[] SyntheticKey(int seed)
    {
        byte[] key = new byte[16];
        new Random(seed).NextBytes(key);
        return key;
    }
}
