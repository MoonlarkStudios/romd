using NSubstitute;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Delivery.Queries.GetConsumerPlatformBios;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Common.Models;
using Romd.Domain.Hashing;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Consumer.Delivery;

public sealed class GetConsumerPlatformBiosQueryHandlerTests
{
    private const int LibraryId = 10;
    private const string PlatformShortName = "psx";
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset GrantExpiresAt = new(2026, 6, 2, 12, 10, 0, TimeSpan.Zero);

    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IConsumerBiosRepository _biosRepository = Substitute.For<IConsumerBiosRepository>();
    private readonly IConsumerBiosGrantIssuer _biosGrantIssuer = Substitute.For<IConsumerBiosGrantIssuer>();

    public GetConsumerPlatformBiosQueryHandlerTests()
    {
        _currentUser.IsAuthenticated.Returns(true);
        _currentUser.UserId.Returns(UserId);
        _currentUser.LibraryId.Returns(999);
    }

    [Fact]
    public async Task HandleAsync_UnauthenticatedUser_ReturnsCurrentUserRequired()
    {
        _currentUser.IsAuthenticated.Returns(false);
        _currentUser.UserId.Returns((Guid?)null);

        var result = await CreateHandler().HandleAsync(new GetConsumerPlatformBiosQuery(PlatformShortName));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ConsumerErrors.CurrentUserRequired().Code);
    }

    [Fact]
    public async Task HandleAsync_NoLiveLibraryAssignment_ReturnsLibraryNotFound()
    {
        SetResult(new ConsumerLibraryReadResult<ConsumerPlatformBios>.LibraryUnavailable());

        var result = await CreateHandler().HandleAsync(new GetConsumerPlatformBiosQuery(PlatformShortName));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ConsumerErrors.LibraryNotFound().Code);
    }

    [Fact]
    public async Task HandleAsync_UnknownPlatform_ReturnsPlatformNotFound()
    {
        SetResult(new ConsumerLibraryReadResult<ConsumerPlatformBios>.ItemNotFound(LibraryId));

        var result = await CreateHandler().HandleAsync(new GetConsumerPlatformBiosQuery(PlatformShortName));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ConsumerErrors.PlatformNotFound().Code);
    }

    [Fact]
    public async Task HandleAsync_InconsistentProjection_ReturnsPlatformNotFoundWithoutGrant()
    {
        SetResult(new ConsumerLibraryReadResult<ConsumerPlatformBios>.ProjectionInconsistent());

        var result = await CreateHandler().HandleAsync(new GetConsumerPlatformBiosQuery(PlatformShortName));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ConsumerErrors.PlatformNotFound().Code);
        _biosGrantIssuer.DidNotReceive().IssueBiosDownloadGrant(Arg.Any<ConsumerBiosGrant>());
    }

    [Fact]
    public async Task HandleAsync_PlatformNotExposed_ReturnsPlatformNotFound()
    {
        SetUpPlatformBios(new ConsumerPlatformBios(1, PlatformShortName, IsExposed: false, []));

        var result = await CreateHandler().HandleAsync(new GetConsumerPlatformBiosQuery(PlatformShortName));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ConsumerErrors.PlatformNotFound().Code);
    }

    [Fact]
    public async Task HandleAsync_ExposedPlatform_MapsOneItemPerFile()
    {
        var availableFile = AvailableFile();
        var unavailableFile = UnavailableFile();
        SetUpPlatformBios(new ConsumerPlatformBios(
            1,
            PlatformShortName,
            IsExposed: true,
            [availableFile, unavailableFile]));
        SetUpIssuedGrant();

        var result = await CreateHandler().HandleAsync(new GetConsumerPlatformBiosQuery(PlatformShortName));

        result.IsError.ShouldBeFalse();
        result.Value.SystemKey.ShouldBe(PlatformShortName);
        result.Value.Items.Count.ShouldBe(2);

        var availableItem = result.Value.Items[0];
        availableItem.BiosId.ShouldBe(IdCoder.Encode(availableFile.BiosId));
        availableItem.Name.ShouldBe(availableFile.BiosName);
        availableItem.FileName.ShouldBe(availableFile.FileName);
        availableItem.SizeBytes.ShouldBe((ByteCount)availableFile.SizeBytes);
        availableItem.Sha1.ShouldBe(availableFile.Sha1?.ToString());
        availableItem.Md5.ShouldBe(availableFile.Md5?.ToString());
        availableItem.Sha256.ShouldBe(availableFile.Sha256?.ToString());
        availableItem.IsAvailable.ShouldBeTrue();
        availableItem.ContentGrant.ShouldNotBeNull();
        availableItem.ContentGrant.DownloadUrl.ShouldBe("/delivery/bios/token-abc");
        availableItem.ContentGrant.ExpiresAt.ShouldBe(GrantExpiresAt);

        var unavailableItem = result.Value.Items[1];
        unavailableItem.BiosId.ShouldBe(IdCoder.Encode(unavailableFile.BiosId));
        unavailableItem.Name.ShouldBe(unavailableFile.BiosName);
        unavailableItem.FileName.ShouldBe(unavailableFile.FileName);
        unavailableItem.SizeBytes.ShouldBe((ByteCount)unavailableFile.SizeBytes);
        unavailableItem.Sha256.ShouldBeNull();
        unavailableItem.IsAvailable.ShouldBeFalse();
        unavailableItem.ContentGrant.ShouldBeNull();
    }

    [Fact]
    public async Task HandleAsync_AvailableFile_IssuesBiosGrantWithGrantClaims()
    {
        var availableFile = AvailableFile();
        SetUpPlatformBios(new ConsumerPlatformBios(1, PlatformShortName, IsExposed: true, [availableFile]));
        SetUpIssuedGrant();

        var result = await CreateHandler().HandleAsync(new GetConsumerPlatformBiosQuery(PlatformShortName));

        result.IsError.ShouldBeFalse();
        _biosGrantIssuer.Received(1).IssueBiosDownloadGrant(new ConsumerBiosGrant(
            UserId,
            LibraryId,
            availableFile.BiosId,
            availableFile.FileId!.Value,
            availableFile.Sha256!.Value,
            availableFile.ContentSizeBytes!.Value));

        await _biosRepository.Received(1).GetPlatformBiosAsync(
            new ConsumerPlatformBiosRequest(new ConsumerLibraryScope(UserId), PlatformShortName),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_UnavailableFiles_DoesNotIssueGrants()
    {
        SetUpPlatformBios(new ConsumerPlatformBios(1, PlatformShortName, IsExposed: true, [UnavailableFile()]));

        var result = await CreateHandler().HandleAsync(new GetConsumerPlatformBiosQuery(PlatformShortName));

        result.IsError.ShouldBeFalse();
        _biosGrantIssuer.DidNotReceive().IssueBiosDownloadGrant(Arg.Any<ConsumerBiosGrant>());
    }

    [Fact]
    public async Task HandleAsync_AvailableFileWithoutStoredSha256_OmitsGrant()
    {
        var fileWithoutSha256 = AvailableFile() with { Sha256 = null };
        SetUpPlatformBios(new ConsumerPlatformBios(1, PlatformShortName, IsExposed: true, [fileWithoutSha256]));

        var result = await CreateHandler().HandleAsync(new GetConsumerPlatformBiosQuery(PlatformShortName));

        result.IsError.ShouldBeFalse();
        var item = result.Value.Items.ShouldHaveSingleItem();
        item.IsAvailable.ShouldBeTrue();
        item.ContentGrant.ShouldBeNull();
        _biosGrantIssuer.DidNotReceive().IssueBiosDownloadGrant(Arg.Any<ConsumerBiosGrant>());
    }

    private GetConsumerPlatformBiosQueryHandler CreateHandler() =>
        new(_currentUser, _biosRepository, _biosGrantIssuer);

    private void SetUpPlatformBios(ConsumerPlatformBios platformBios) =>
        SetResult(new ConsumerLibraryReadResult<ConsumerPlatformBios>.Found(LibraryId, platformBios));

    private void SetResult(ConsumerLibraryReadResult<ConsumerPlatformBios> result) =>
        _biosRepository
            .GetPlatformBiosAsync(
                new ConsumerPlatformBiosRequest(new ConsumerLibraryScope(UserId), PlatformShortName),
                Arg.Any<CancellationToken>())
            .Returns(result);

    private void SetUpIssuedGrant() =>
        _biosGrantIssuer
            .IssueBiosDownloadGrant(Arg.Any<ConsumerBiosGrant>())
            .Returns(new ConsumerIssuedContentGrant("/delivery/bios/token-abc", GrantExpiresAt));

    private static ConsumerBiosFile AvailableFile() =>
        new(
            BiosId: 5,
            BiosName: "PlayStation BIOS (USA)",
            FileName: "scph5501.bin",
            SizeBytes: 524_288,
            Sha1: HashWithFirstByte<Sha1>(Sha1.ByteLength, 0x01),
            Md5: HashWithFirstByte<Md5>(Md5.ByteLength, 0x02),
            FileId: 40,
            Sha256: HashWithFirstByte<Sha256>(Sha256.ByteLength, 0x03),
            ContentSizeBytes: 524_288);

    private static ConsumerBiosFile UnavailableFile() =>
        new(
            BiosId: 6,
            BiosName: "PlayStation BIOS (Japan)",
            FileName: "scph1000.bin",
            SizeBytes: 524_288,
            Sha1: HashWithFirstByte<Sha1>(Sha1.ByteLength, 0x04),
            Md5: null,
            FileId: null,
            Sha256: null,
            ContentSizeBytes: null);

    private static T HashWithFirstByte<T>(int byteLength, byte firstByte)
        where T : struct, IHashValue<T>
    {
        var bytes = new byte[byteLength];
        bytes[0] = firstByte;
        return T.FromSpan(bytes);
    }
}
