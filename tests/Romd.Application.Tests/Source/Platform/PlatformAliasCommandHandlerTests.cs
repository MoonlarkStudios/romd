using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;
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
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(5.5)).AddTicks(1234567);
    private readonly IPlatformAliasRepository _aliasRepository = Substitute.For<IPlatformAliasRepository>();
    private readonly IPlatformRepository _platformRepository = Substitute.For<IPlatformRepository>();

    private readonly IUnitOfWork _work = Substitute.For<IUnitOfWork>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();

    public AddPlatformAliasCommandHandlerTests()
    {
        _work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_transaction);
    }

    private AddPlatformAliasCommandHandler CreateHandler(TimeProvider? clock = null) =>
        new(_platformRepository, _aliasRepository, _work, NullLogger<AddPlatformAliasCommandHandler>.Instance, clock ?? new FixedClock());

    private void PlatformExists(int id) =>
        _platformRepository.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Domain.Source.Platform.Platform.CreateNew("Some Platform", "some-platform", now: DateTimeOffset.UnixEpoch));

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
        _aliasRepository.AddStagedAsync(Arg.Any<PlatformAlias>(), Arg.Any<CancellationToken>())
            .Returns(call => Receipt(call.Arg<PlatformAlias>()));

        var command = AddPlatformAliasCommand.Create(1, "name", "Super Famicom").Value;
        var result = await CreateHandler().HandleAsync(command);

        result.IsError.ShouldBeFalse();
        result.Value.Type.ShouldBe(PlatformAliasType.Name);
        result.Value.NormalizedValue.ShouldBe("super famicom");
        result.Value.CreatedAt.Ticks.ShouldBe(Now.Ticks);
        result.Value.CreatedAt.Offset.ShouldBe(Now.Offset);
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
        _aliasRepository.AddStagedAsync(Arg.Any<PlatformAlias>(), Arg.Any<CancellationToken>())
            .Returns(call => Receipt(call.Arg<PlatformAlias>()));

        var command = AddPlatformAliasCommand.Create(1, "provider", "19", "IGDB").Value;
        var result = await CreateHandler().HandleAsync(command);

        result.IsError.ShouldBeFalse();
        result.Value.Type.ShouldBe(PlatformAliasType.ProviderMapping);
        result.Value.Provider.ShouldBe("igdb");
        result.Value.Value.ShouldBe("19");
        result.Value.CreatedAt.Ticks.ShouldBe(Now.Ticks);
        result.Value.CreatedAt.Offset.ShouldBe(Now.Offset);
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_Creation_PreservesValidationClockAndBoundaryOrder(bool provider)
    {
        var order = new List<string>();
        var created = PlatformAlias.Rehydrate(21, 1, provider ? PlatformAliasType.ProviderMapping : PlatformAliasType.Name,
            "19", "19", provider ? "igdb" : null, Now);
        var receipt = Substitute.For<IStagedPlatformAliasCreation>();
        receipt.ReadAfterFlush().Returns(_ => { order.Add("receipt"); return created; });
        _work.When(work => work.BeginTransactionAsync(Arg.Any<CancellationToken>())).Do(_ => order.Add("begin"));
        PlatformExists(1);
        _platformRepository.When(repo => repo.GetByIdAsync(1, Arg.Any<CancellationToken>())).Do(_ => order.Add("platform"));
        _aliasRepository.When(repo => repo.NameAliasExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("duplicate"));
        _aliasRepository.When(repo => repo.ProviderMappingExistsAsync(1, Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("duplicate"));
        _aliasRepository.AddStagedAsync(Arg.Any<PlatformAlias>(), Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("stage"); return receipt; });
        _work.When(work => work.FlushAsync(Arg.Any<CancellationToken>())).Do(_ => order.Add("flush"));
        _transaction.When(transaction => transaction.CommitAsync(Arg.Any<CancellationToken>())).Do(_ => order.Add("commit"));

        var result = await CreateHandler(new ObservedClock(() => order.Add("clock")))
            .HandleAsync(AddPlatformAliasCommand.Create(1, provider ? "provider" : "name", "19", "igdb").Value);

        result.IsError.ShouldBeFalse();
        result.Value.Id.ShouldBe(21);
        order.ShouldBe(provider
            ? ["begin", "platform", "duplicate", "clock", "stage", "flush", "receipt", "commit"]
            : ["begin", "platform", "clock", "duplicate", "stage", "flush", "receipt", "commit"]);
    }

    [Fact]
    public async Task HandleAsync_PlatformMissing_PrecedesDuplicateChecksAndDoesNotFlushOrCommit()
    {
        _aliasRepository.NameAliasExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await CreateHandler().HandleAsync(AddPlatformAliasCommand.Create(42, "name", "SNES").Value);

        result.FirstError.Code.ShouldBe("Catalog.PlatformNotFound");
        await _aliasRepository.DidNotReceive().NameAliasExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _work.DidNotReceive().FlushAsync(Arg.Any<CancellationToken>());
        await _transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    private static IStagedPlatformAliasCreation Receipt(PlatformAlias alias)
    {
        var receipt = Substitute.For<IStagedPlatformAliasCreation>();
        receipt.ReadAfterFlush().Returns(PlatformAlias.Rehydrate(21, alias.PlatformId, alias.Type, alias.Value,
            alias.NormalizedValue, alias.Provider, alias.CreatedAt));
        return receipt;
    }

    private sealed class ObservedClock(Action observe) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() { observe(); return Now; }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
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
            .Returns(Domain.Source.Platform.Platform.CreateNew("Some Platform", "some-platform", now: DateTimeOffset.UnixEpoch));
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
            .Returns(Domain.Source.Platform.Platform.CreateNew("Some Platform", "some-platform", now: DateTimeOffset.UnixEpoch));
        _aliasRepository.GetByIdAsync(9, Arg.Any<CancellationToken>())
            .Returns(PlatformAlias.Rehydrate(9, 2, PlatformAliasType.Name, "SNES", "snes", null, DateTimeOffset.UtcNow));

        var result = await CreateHandler().HandleAsync(new RemovePlatformAliasCommand(1, 9));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Platform.AliasNotFound");
        await _aliasRepository.DidNotReceive().RemoveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
