using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Source.Platform.Commands.AddPlatformAlias;
using Romd.Admin.Application.Source.Platform.Commands.RemovePlatformAlias;
using Romd.Domain.Source.Platform;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Platform;

public sealed class AddPlatformAliasCommandHandlerTests
{
    private readonly IPlatformAliasRepository _aliasRepository = Substitute.For<IPlatformAliasRepository>();
    private readonly IPlatformRepository _platformRepository = Substitute.For<IPlatformRepository>();

    private AddPlatformAliasCommandHandler CreateHandler() => new(_platformRepository, _aliasRepository);

    private void PlatformExists(int id) =>
        _platformRepository.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Domain.Source.Platform.Platform.CreateNew("Some Platform", "some-platform"));

    [Fact]
    public void Create_NameType_BuildsNameCommand()
    {
        var result = AddPlatformAliasCommand.Create(1, "name", "Super Famicom");

        result.IsError.ShouldBeFalse();
        result.Value.Type.ShouldBe(PlatformAliasType.Name);
        result.Value.Provider.ShouldBeNull();
    }

    [Fact]
    public void Create_ProviderTypeWithoutProvider_ReturnsValidationError()
    {
        var result = AddPlatformAliasCommand.Create(1, "provider", "19");

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Command.MissingProvider");
    }

    [Fact]
    public void Create_UnknownType_ReturnsValidationError()
    {
        var result = AddPlatformAliasCommand.Create(1, "nickname", "SNES");

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Platform.InvalidAliasType");
    }

    [Fact]
    public async Task HandleAsync_PlatformMissing_ReturnsNotFound()
    {
        _platformRepository.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns((Domain.Source.Platform.Platform?)null);

        var command = AddPlatformAliasCommand.Create(42, "name", "SNES").Value;
        var result = await CreateHandler().HandleAsync(command);

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorType.NotFound);
    }

    [Fact]
    public async Task HandleAsync_NewNameAlias_Adds()
    {
        PlatformExists(1);
        _aliasRepository.NameAliasExistsAsync("super famicom", Arg.Any<CancellationToken>()).Returns(false);
        _aliasRepository.AddAsync(Arg.Any<PlatformAlias>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<PlatformAlias>());

        var command = AddPlatformAliasCommand.Create(1, "name", "Super Famicom").Value;
        var result = await CreateHandler().HandleAsync(command);

        result.IsError.ShouldBeFalse();
        result.Value.Type.ShouldBe(PlatformAliasType.Name);
        result.Value.NormalizedValue.ShouldBe("super famicom");
    }

    [Fact]
    public async Task HandleAsync_DuplicateNameAlias_ReturnsConflict()
    {
        PlatformExists(1);
        _aliasRepository.NameAliasExistsAsync("super famicom", Arg.Any<CancellationToken>()).Returns(true);

        var command = AddPlatformAliasCommand.Create(1, "name", "Super  Famicom").Value;
        var result = await CreateHandler().HandleAsync(command);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Platform.AliasAlreadyExists");
    }

    [Fact]
    public async Task HandleAsync_NewProviderMapping_Adds()
    {
        PlatformExists(1);
        _aliasRepository.ProviderMappingExistsAsync(1, "igdb", Arg.Any<CancellationToken>()).Returns(false);
        _aliasRepository.AddAsync(Arg.Any<PlatformAlias>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<PlatformAlias>());

        var command = AddPlatformAliasCommand.Create(1, "provider", "19", "IGDB").Value;
        var result = await CreateHandler().HandleAsync(command);

        result.IsError.ShouldBeFalse();
        result.Value.Type.ShouldBe(PlatformAliasType.ProviderMapping);
        result.Value.Provider.ShouldBe("igdb");
        result.Value.Value.ShouldBe("19");
    }

    [Fact]
    public async Task HandleAsync_DuplicateProviderMapping_ReturnsConflict()
    {
        PlatformExists(1);
        _aliasRepository.ProviderMappingExistsAsync(1, "igdb", Arg.Any<CancellationToken>()).Returns(true);

        var command = AddPlatformAliasCommand.Create(1, "provider", "19", "igdb").Value;
        var result = await CreateHandler().HandleAsync(command);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Platform.ProviderMappingAlreadyExists");
    }
}

public sealed class RemovePlatformAliasCommandHandlerTests
{
    private readonly IPlatformAliasRepository _aliasRepository = Substitute.For<IPlatformAliasRepository>();
    private readonly IPlatformRepository _platformRepository = Substitute.For<IPlatformRepository>();

    private RemovePlatformAliasCommandHandler CreateHandler() => new(_platformRepository, _aliasRepository);

    [Fact]
    public async Task HandleAsync_AliasOnPlatform_Removes()
    {
        _platformRepository.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(Domain.Source.Platform.Platform.CreateNew("Some Platform", "some-platform"));
        _aliasRepository.GetByIdAsync(9, Arg.Any<CancellationToken>())
            .Returns(PlatformAlias.Rehydrate(9, 1, PlatformAliasType.Name, "SNES", "snes", null, DateTimeOffset.UtcNow));

        var result = await CreateHandler().HandleAsync(new RemovePlatformAliasCommand(1, 9));

        result.IsError.ShouldBeFalse();
        await _aliasRepository.Received(1).RemoveAsync(9, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_AliasBelongsToOtherPlatform_ReturnsNotFound()
    {
        _platformRepository.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(Domain.Source.Platform.Platform.CreateNew("Some Platform", "some-platform"));
        _aliasRepository.GetByIdAsync(9, Arg.Any<CancellationToken>())
            .Returns(PlatformAlias.Rehydrate(9, 2, PlatformAliasType.Name, "SNES", "snes", null, DateTimeOffset.UtcNow));

        var result = await CreateHandler().HandleAsync(new RemovePlatformAliasCommand(1, 9));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Platform.AliasNotFound");
        await _aliasRepository.DidNotReceive().RemoveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
