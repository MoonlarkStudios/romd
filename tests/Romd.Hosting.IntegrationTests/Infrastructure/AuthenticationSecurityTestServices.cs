using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Romd.Hosting.Security;
using Romd.Persistence.Identity;

namespace Romd.Hosting.IntegrationTests.Infrastructure;

internal sealed class AuthenticationSecurityTestServices
{
    public ManualTimeProvider Time { get; } = new();
    public CountingPasswordHasher Hasher { get; } = new();

    public void Configure(IServiceCollection services, string peer = "192.0.2.100")
    {
        services.RemoveAll<AuthenticationAttemptOptions>();
        services.AddSingleton(new AuthenticationAttemptOptions());
        services.RemoveAll<AuthenticationAttemptLimiter>();
        services.AddSingleton<AuthenticationAttemptLimiter>();
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<IPasswordHasher<RomdUser>>(Hasher);
        services.AddTransient<IStartupFilter>(_ => new PeerAddressStartupFilter(IPAddress.Parse(peer)));
    }

    public sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan delta) => _timestamp += delta.Ticks;
    }

    public sealed class CountingPasswordHasher : IPasswordHasher<RomdUser>
    {
        private readonly PasswordHasher<RomdUser> _inner = new();
        public int Verifications { get; private set; }
        public string HashPassword(RomdUser user, string password) => _inner.HashPassword(user, password);
        public PasswordVerificationResult VerifyHashedPassword(RomdUser user, string hashedPassword, string providedPassword)
        {
            Verifications++;
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    // The simulated peer is test-owned connection metadata, independent of request headers.
    private sealed class PeerAddressStartupFilter(IPAddress peer) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                context.Connection.RemoteIpAddress = peer;
                return continuation(context);
            });
            next(app);
        };
    }
}
