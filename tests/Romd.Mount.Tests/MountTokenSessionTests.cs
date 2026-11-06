using System.Net;
using System.Text.Json;
using Romd.Mount.Credentials;
using Shouldly;
using Xunit;

namespace Romd.Mount.Tests;

public sealed class MountTokenSessionTests
{
    [Fact]
    public async Task ConcurrentRequests_WaitForPersistenceAndShareOneRotation()
    {
        using var host = new Harness();
        host.Store.WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string>[] requests = Enumerable.Range(0, 12).Select(_ => host.Session.GetAccessTokenAsync()).ToArray();
        await host.Store.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        requests.ShouldAllBe(task => !task.IsCompleted);
        host.Store.WriteGate.SetResult();
        var tokens = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
        tokens.ShouldAllBe(token => token == "access-1");
        host.Handler.Refreshes.ShouldBe(1);
        host.Store.Value.ShouldBe("refresh-1");
    }

    [Fact]
    public async Task PersistenceFailure_RetainsRotatedTokenAndRetriesSaveBeforeReturning()
    {
        using var host = new Harness();
        host.Store.FailWrite = true;
        await Should.ThrowAsync<MountCredentialException>(() => host.Session.GetAccessTokenAsync());
        host.Store.Value.ShouldBe("initial");
        host.Handler.Refreshes.ShouldBe(1);
        host.Store.FailWrite = false;
        (await host.Session.GetAccessTokenAsync()).ShouldBe("access-1");
        host.Store.Value.ShouldBe("refresh-1");
        host.Handler.Refreshes.ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentUnauthorizedResponses_RotateRejectedTokenOnlyOnce()
    {
        using var host = new Harness();
        string rejected = await host.Session.GetAccessTokenAsync();
        var replacements = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => host.Session.GetAccessTokenAsync(rejected)));
        replacements.ShouldAllBe(token => token == "access-2");
        host.Handler.Refreshes.ShouldBe(2);
    }

    [Fact]
    public async Task UnauthorizedThenNetworkFailure_DoesNotReuseRejectedCachedToken()
    {
        using var host = new Harness();
        string rejected = await host.Session.GetAccessTokenAsync();
        host.Handler.Offline = true;
        await Should.ThrowAsync<MountIdentityException>(() => host.Session.GetAccessTokenAsync(rejected));
        host.Handler.Offline = false;
        (await host.Session.GetAccessTokenAsync()).ShouldBe("access-2");
    }

    [Fact]
    public async Task Expiry_RenewsUsingPersistedRotation()
    {
        var clock = new Clock();
        using var host = new Harness(clock);
        (await host.Session.GetAccessTokenAsync()).ShouldBe("access-1");
        clock.Seconds = 850;
        (await host.Session.GetAccessTokenAsync()).ShouldBe("access-1");
        clock.Seconds = 880;
        (await host.Session.GetAccessTokenAsync()).ShouldBe("access-2");
        host.Handler.Refreshes.ShouldBe(2);
        host.Store.Value.ShouldBe("refresh-2");
    }

    [Fact]
    public async Task CancellationDuringSave_RetriesPersistenceWithoutLosingRotation()
    {
        using var host = new Harness();
        host.Store.WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var request = host.Session.GetAccessTokenAsync(ct: cancel.Token);
        await host.Store.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => request);
        host.Store.WriteGate.SetResult();
        (await host.Session.GetAccessTokenAsync()).ShouldBe("access-1");
        host.Handler.Refreshes.ShouldBe(1);
        host.Store.Value.ShouldBe("refresh-1");
    }

    [Fact]
    public async Task ChangedServer_PreventsCredentialStoreRead()
    {
        using var host = new Harness();
        host.Handler.Instance = Guid.NewGuid();
        (await Should.ThrowAsync<MountIdentityException>(() => host.Session.GetAccessTokenAsync()))
            .Failure.ShouldBe(IdentityFailure.ServerChanged);
        host.Store.Reads.ShouldBe(0);
        host.Handler.Refreshes.ShouldBe(0);
    }

    private sealed class Harness : IDisposable
    {
        public readonly IdentityHandler Handler = new();
        public readonly Store Store = new();
        public MountTokenSession Session { get; }
        private readonly MountIdentityClient _client;
        public Harness(TimeProvider? clock = null)
        {
            _client = new(new Uri("https://server.example"), Handler);
            var bound = new BoundMountIdentity(_client, Handler.Instance);
            Session = new(bound, new MountCredentialScope(bound, "profile"), Store, clock);
        }
        public void Dispose() => _client.Dispose();
    }

    private sealed class Clock : TimeProvider
    {
        public long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }

    private sealed class Store : IMountCredentialStore
    {
        public string Value = "initial";
        public int Reads;
        public bool FailWrite;
        public TaskCompletionSource WriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? WriteGate;
        public Task<string?> ReadAsync(MountCredentialScope scope, CancellationToken ct = default)
        { Reads++; return Task.FromResult<string?>(Value); }
        public async Task WriteAsync(MountCredentialScope scope, string refreshToken, CancellationToken ct = default)
        {
            WriteStarted.TrySetResult();
            if (WriteGate is not null)
                await WriteGate.Task.WaitAsync(ct);
            if (FailWrite)
                throw new MountCredentialException(-1);
            Value = refreshToken;
        }
        public Task DeleteAsync(MountCredentialScope scope, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class IdentityHandler : HttpMessageHandler
    {
        public Guid Instance = Guid.NewGuid();
        public int Refreshes;
        public bool Offline;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            object value;
            if (request.Method == HttpMethod.Get)
                value = new { instanceId = Instance.ToString("D") };
            else
            {
                Refreshes++;
                value = new
                {
                    access_token = "access-" + Refreshes,
                    refresh_token = "refresh-" + Refreshes,
                    token_type = "Bearer",
                    expires_in = 900
                };
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) });
        }
    }
}
