using System.Collections.Concurrent;
using System.Security.Claims;
using ErrorOr;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Application.Common.Security.Commands.StartAccountSession;
using Romd.Persistence;
using Shouldly;

namespace Romd.Hosting.IntegrationTests.Infrastructure;

public enum SessionAcceptanceFault { None, BeforeAcceptance, InteractiveCookie, ProtocolIssuance }

/// <summary>Observes real command commits and real authentication dispatch; never fakes persistence or commit success.</summary>
internal sealed class AccountSessionIssuanceProbe
{
    private int starts;
    public int Starts => Volatile.Read(ref starts);
    public SessionAcceptanceFault Fault { get; set; }
    public ConcurrentQueue<AccountSession> Accepted { get; } = new();
    public ConcurrentQueue<(string Scheme, Guid SessionId)> IssuanceAttempts { get; } = new();

    public void Configure(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Scoped<ICommandHandler<StartAccountSessionCommand, AccountSession>>(sp =>
            new ObservedCommand(ActivatorUtilities.CreateInstance<StartAccountSessionCommandHandler>(sp), sp, this)));
        var original = services.Last(descriptor => descriptor.ServiceType == typeof(IAuthenticationService));
        services.Remove(original);
        services.Add(ServiceDescriptor.Describe(typeof(IAuthenticationService), sp =>
        {
            var inner = original.ImplementationInstance as IAuthenticationService
                ?? original.ImplementationFactory?.Invoke(sp) as IAuthenticationService
                ?? (IAuthenticationService)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
            return new ObservedAuthentication(inner, sp, this);
        }, original.Lifetime));
    }

    private sealed class ObservedCommand(StartAccountSessionCommandHandler inner, IServiceProvider services,
        AccountSessionIssuanceProbe probe) : ICommandHandler<StartAccountSessionCommand, AccountSession>
    {
        public async Task<ErrorOr<AccountSession>> HandleAsync(StartAccountSessionCommand command, CancellationToken ct = default)
        {
            Interlocked.Increment(ref probe.starts);
            if (probe.Fault == SessionAcceptanceFault.BeforeAcceptance)
                throw new IOException("injected session acceptance failure");
            var result = await inner.HandleAsync(command, ct);
            result.IsError.ShouldBeFalse();
            services.GetRequiredService<RomdDbContext>().Database.CurrentTransaction.ShouldBeNull();
            await using var read = new RomdDbContext(services.GetRequiredService<DbContextOptions<RomdDbContext>>());
            var persisted = await read.AccountSessions.SingleAsync(row => row.Id == result.Value.Id, ct);
            persisted.UserId.ShouldBe(command.UserId);
            persisted.ClientId.ShouldBe(command.ClientId);
            persisted.AccountStamp.ShouldBe(command.AccountStamp);
            probe.Accepted.Enqueue(result.Value);
            return result;
        }
    }

    private sealed class ObservedAuthentication(IAuthenticationService inner, IServiceProvider services,
        AccountSessionIssuanceProbe probe) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => inner.AuthenticateAsync(context, scheme);
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => inner.ChallengeAsync(context, scheme, properties);
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => inner.ForbidAsync(context, scheme, properties);
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => inner.SignOutAsync(context, scheme, properties);
        public async Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            if (Guid.TryParse(principal.FindFirst(SessionClaims.Id)?.Value, out var sessionId))
            {
                await using var read = new RomdDbContext(services.GetRequiredService<DbContextOptions<RomdDbContext>>());
                (await read.AccountSessions.AnyAsync(row => row.Id == sessionId, context.RequestAborted)).ShouldBeTrue();
                probe.IssuanceAttempts.Enqueue((scheme ?? "", sessionId));
                bool cookie = scheme == Romd.Hosting.RomdHostRegistrationExtensions.InteractiveLoginScheme;
                if (probe.Fault == (cookie ? SessionAcceptanceFault.InteractiveCookie : SessionAcceptanceFault.ProtocolIssuance))
                    throw new IOException(cookie ? "injected cookie issuance failure" : "injected protocol issuance failure");
            }
            await inner.SignInAsync(context, scheme, principal, properties);
        }
    }
}
