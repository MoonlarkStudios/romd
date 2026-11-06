using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Application.Common.Security.Commands.StartAccountSession;
using Romd.Persistence;
using Romd.Persistence.Identity;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Identity;

public sealed class AccountSessionCompositionTests
{
    [Theory]
    [InlineData("admin")]
    [InlineData("consumer")]
    [InlineData("testing")]
    public async Task ScopedAuthenticationServices_AliasOneStoreAndResolveSharedCommand(string composition)
    {
        // Resolution constructs contexts but never opens a database connection.
        var services = AccountSessionCreationAtomicityTests.Services(composition,
            "Host=localhost;Database=not-opened;Username=not-opened;Password=not-opened");
        await using var provider = services.BuildServiceProvider();
        await using var first = provider.CreateAsyncScope();
        var canonical = first.ServiceProvider.GetRequiredService<AccountSessionStore>();
        first.ServiceProvider.GetRequiredService<IAccountSessions>().ShouldBeSameAs(canonical);
        first.ServiceProvider.GetRequiredService<IAccountSessionCreationStore>().ShouldBeSameAs(canonical);
        first.ServiceProvider.GetRequiredService<IAccountSessionTransaction>().ShouldBeOfType<AccountSessionTransaction>();
        first.ServiceProvider.GetRequiredService<ICommandHandler<StartAccountSessionCommand, AccountSession>>()
            .ShouldBeOfType<StartAccountSessionCommandHandler>();
        await using var second = provider.CreateAsyncScope();
        second.ServiceProvider.GetRequiredService<AccountSessionStore>().ShouldNotBeSameAs(canonical);
        second.ServiceProvider.GetRequiredService<RomdDbContext>().ShouldNotBeSameAs(first.ServiceProvider.GetRequiredService<RomdDbContext>());
    }

    [Fact]
    public void SessionAuthorityPort_DoesNotGrantCreation_AndCreationPortCannotCommit()
    {
        typeof(IAccountSessions).GetMethod("StartAsync").ShouldBeNull();
        typeof(IAccountSessionCreationStore).GetMethods().Select(method => method.Name).ShouldBe(["StageStartAsync"]);
        typeof(IAccountSessionCreationStore).GetMethod("StageStartAsync").ShouldNotBeNull().ReturnType.ShouldBe(typeof(Task<AccountSession>));
    }
}
