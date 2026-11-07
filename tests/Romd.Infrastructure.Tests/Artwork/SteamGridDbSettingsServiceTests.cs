using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ErrorOr;
using Romd.Admin.Application.Artwork.Commands.ReplaceSteamGridDbSettings;
using Romd.Admin.Application.Artwork.Commands.TestSteamGridDbConnection;
using Romd.Application.Common.Concurrency;
using Romd.Contracts.Management.Artwork;
using Romd.Infrastructure.Artwork;
using Romd.Persistence;
using Romd.Persistence.Artwork;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed class SteamGridDbSettingsServiceTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();

    private SettingsHarness Create(RomdDbContext db, ProbeHandler? handler = null,
        SteamGridDbOptions? options = null, IDataProtectionProvider? protection = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(SteamGridDbSettingsService.HttpClientName).Returns(_ => new HttpClient(handler ?? new(), false));
        return new(new SteamGridDbSettingsService(new SteamGridDbSettingsStore(db), protection ?? _protection,
            Options.Create(options ?? new()), factory, TimeProvider.System), new EfUnitOfWork(db));
    }

    [Fact]
    public async Task UpdateAsync_Key_EncryptedWriteOnlyAndSharedAcrossScopes()
    {
        await using var db = _database.CreateContext();
        var service = Create(db);
        var updated = await service.UpdateAsync(new(Guid.Empty, true, "private-key"));
        updated.IsError.ShouldBeFalse();
        updated.Value.IsConfigured.ShouldBeTrue();
        JsonSerializer.Serialize(updated.Value).ShouldNotContain("private-key");
        new UpdateSteamGridDbSettingsRequest(true, "private-key").ToString().ShouldNotContain("private-key");
        var row = await db.Set<MetadataProviderSettingsEntity>().SingleAsync(x => x.ProviderId == "steamgriddb");
        row.ProtectedClientSecret.ShouldNotBeNull().ShouldNotContain("private-key");
        await using var nextDb = _database.CreateContext();
        (await Create(nextDb).GetApiKeyAsync()).ShouldBe("private-key");
    }

    [Fact]
    public async Task UpdateAsync_DisableRetainReplaceClearAndStaleRevision_ExplicitSemantics()
    {
        await using var db = _database.CreateContext();
        var service = Create(db);
        var first = (await service.UpdateAsync(new(Guid.Empty, true, "first"))).Value;
        (await service.UpdateAsync(new(Guid.Empty, true, "stale"))).FirstError.Code.ShouldBe("SteamGridDb.PreconditionFailed");
        var disabled = (await service.UpdateAsync(new(first.Revision, false, null))).Value;
        disabled.HasApiKey.ShouldBeTrue();
        (await service.GetApiKeyAsync()).ShouldBeNull();
        var replaced = (await service.UpdateAsync(new(disabled.Revision, true, "replacement"))).Value;
        (await service.GetApiKeyAsync()).ShouldBe("replacement");
        (await service.UpdateAsync(new(replaced.Revision, true, null, true))).IsError.ShouldBeTrue();
        (await service.UpdateAsync(new(replaced.Revision, false, "conflict", true))).IsError.ShouldBeTrue();
        var cleared = (await service.UpdateAsync(new(replaced.Revision, false, null, true))).Value;
        cleared.HasApiKey.ShouldBeFalse();
        cleared.IsConfigured.ShouldBeFalse();
    }

    [Theory]
    [InlineData("bad\nkey")]
    [InlineData("bad key")]
    public async Task UpdateAsync_InvalidKey_Rejected(string key)
    {
        await using var db = _database.CreateContext();
        (await Create(db).UpdateAsync(new(Guid.Empty, true, key))).FirstError.Code.ShouldBe("SteamGridDb.InvalidApiKey");
    }

    [Fact]
    public async Task GetAsync_UnreadableKey_FailsClosedAndAllowsRepair()
    {
        await using var db = _database.CreateContext();
        var first = (await Create(db).UpdateAsync(new(Guid.Empty, true, "key"))).Value;
        var broken = Create(db, protection: new EphemeralDataProtectionProvider());
        (await broken.GetApiKeyAsync()).ShouldBeNull();
        (await broken.GetAsync()).ConfigurationError.ShouldNotBeNull().ShouldContain("cannot be decrypted");
        (await broken.UpdateAsync(new(first.Revision, true, null))).IsError.ShouldBeTrue();
        (await broken.UpdateAsync(new(first.Revision, true, "new-key"))).Value.IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public async Task GetAsync_DeploymentOverridesStoredKeyAndEnablement_LocksManagement()
    {
        await using var db = _database.CreateContext();
        var first = (await Create(db).UpdateAsync(new(Guid.Empty, true, "stored"))).Value;
        var service = Create(db, options: new() { ApiKey = "deployment", Enabled = false });
        (await service.GetAsync()).ManagedByDeployment.ShouldBeTrue();
        (await service.GetApiKeyAsync()).ShouldBeNull();
        (await service.UpdateAsync(new(first.Revision, true, "other"))).FirstError.Code.ShouldBe("SteamGridDb.ManagedByDeployment");
        (await Create(db, options: new() { ApiKey = "deployment" }).GetApiKeyAsync()).ShouldBe("deployment");
        (await Create(db, options: new() { ApiKey = "" }).GetAsync()).IsConfigured.ShouldBeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_ValidKey_UsesBearerAndPersistsConfigurationBoundResult()
    {
        await using var db = _database.CreateContext();
        var handler = new ProbeHandler();
        var service = Create(db, handler);
        var initial = (await service.UpdateAsync(new(Guid.Empty, false, "key"))).Value;
        var result = await service.TestConnectionAsync();
        result.LastTestSucceeded.ShouldBe(true);
        result.Revision.ShouldNotBe(initial.Revision);
        (await service.GetAsync()).Revision.ShouldBe(result.Revision);
        handler.Uri.ShouldBe("https://www.steamgriddb.com/api/v2/search/autocomplete/mario");
        handler.Authorization.ShouldBe("Bearer key");
        (await Create(db, options: new() { ApiKey = "other" }).GetAsync()).LastTestedAt.ShouldBeNull();
        (await service.UpdateAsync(new(result.Revision, true, null))).Value.LastTestedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authentication failed")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit")]
    [InlineData(HttpStatusCode.Redirect, "unavailable")]
    public async Task TestConnectionAsync_ProviderFailure_Sanitized(HttpStatusCode status, string expected)
    {
        await using var db = _database.CreateContext();
        var service = Create(db, new() { Status = status, Body = "upstream-secret" });
        await service.UpdateAsync(new(Guid.Empty, true, "key"));
        var result = await service.TestConnectionAsync();
        result.LastTestSucceeded.ShouldBe(false);
        result.LastTestMessage.ShouldNotBeNull().ShouldContain(expected);
        result.LastTestMessage.ShouldNotBeNull().ShouldNotContain("upstream-secret");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"success\":false,\"data\":[]}")]
    [InlineData("{\"success\":true,\"data\":{}}")]
    [InlineData("upstream-secret")]
    public async Task TestConnectionAsync_MalformedBody_FailsWithoutLeakingBody(string body)
    {
        await using var db = _database.CreateContext();
        var service = Create(db, new() { Body = body });
        await service.UpdateAsync(new(Guid.Empty, true, "key"));
        var result = await service.TestConnectionAsync();
        result.LastTestSucceeded.ShouldBe(false);
        result.LastTestMessage.ShouldNotBeNull().ShouldNotContain("upstream-secret");
    }

    [Fact]
    public async Task TestConnectionAsync_OversizedBody_FailsBounded()
    {
        await using var db = _database.CreateContext();
        var service = Create(db, new() { Body = new string('x', 256 * 1024 + 1) });
        await service.UpdateAsync(new(Guid.Empty, true, "key"));
        (await service.TestConnectionAsync()).LastTestSucceeded.ShouldBe(false);
    }

    [Fact]
    public async Task TryUpdateAsync_CompetingRevisions_OnlyFirstUpdateSucceeds()
    {
        await using var firstDb = _database.CreateContext();
        await using var secondDb = _database.CreateContext();
        var first = Create(firstDb);
        var second = Create(secondDb);
        var snapshot = await first.GetAsync();
        (await first.UpdateAsync(new(snapshot.Revision, false, null))).IsError.ShouldBeFalse();
        (await second.UpdateAsync(new(snapshot.Revision, true, "key"))).FirstError.Code.ShouldBe("SteamGridDb.PreconditionFailed");
        (await second.GetAsync()).Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_NetworkException_DoesNotPersistSensitiveDetails()
    {
        await using var db = _database.CreateContext();
        var service = Create(db, new() { Before = _ => throw new HttpRequestException("upstream-secret") });
        await service.UpdateAsync(new(Guid.Empty, true, "key"));
        var result = await service.TestConnectionAsync();
        result.LastTestSucceeded.ShouldBe(false);
        result.LastTestMessage.ShouldNotBeNull().ShouldContain("network connection");
        result.LastTestMessage.ShouldNotBeNull().ShouldNotContain("upstream-secret");
    }

    [Fact]
    public async Task TestConnectionAsync_CallerCancellation_DoesNotRecordFailure()
    {
        await using var db = _database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var service = Create(db, new() { Before = ct => { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } });
        var initial = (await service.UpdateAsync(new(Guid.Empty, true, "key"))).Value;
        await Should.ThrowAsync<OperationCanceledException>(() => service.TestConnectionAsync(cancellation.Token));
        (await service.GetAsync()).LastTestedAt.ShouldBeNull();
        (await service.GetAsync()).Revision.ShouldBe(initial.Revision);
    }

    [Fact]
    public async Task TestConnectionAsync_SettingsChangedDuringProbe_ReturnsPreconditionFailedWithoutStoringResult()
    {
        await using var db = _database.CreateContext();
        await using var editDb = _database.CreateContext();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Create(db, new() { Before = async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); } });
        var first = (await service.UpdateAsync(new(Guid.Empty, true, "key"))).Value;
        var probe = service.TestWithPreconditionAsync(first.Revision);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        { (await Create(editDb).UpdateAsync(new(first.Revision, true, "new-key"))).IsError.ShouldBeFalse(); }
        finally { release.SetResult(); }
        (await probe).FirstError.Code.ShouldBe("SteamGridDb.PreconditionFailed");
        (await service.GetAsync()).LastTestSucceeded.ShouldBeNull();
    }

    [Fact]
    public async Task TestConnectionAsync_CompetingProbes_OnlyFirstStatusCommitsAndNoTransactionSpansNetwork()
    {
        await using var firstDb = _database.CreateContext();
        await using var secondDb = _database.CreateContext();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Create(firstDb, new()
        {
            Before = async ct =>
        {
            firstDb.Database.CurrentTransaction.ShouldBeNull();
            firstEntered.SetResult();
            await firstRelease.Task.WaitAsync(ct);
        }
        });
        var second = Create(secondDb, new()
        {
            Before = async ct =>
        {
            secondDb.Database.CurrentTransaction.ShouldBeNull();
            secondEntered.SetResult();
            await secondRelease.Task.WaitAsync(ct);
        }
        });
        var saved = (await first.UpdateAsync(new(Guid.Empty, true, "key"))).Value;
        var firstProbe = first.TestWithPreconditionAsync(saved.Revision);
        var secondProbe = second.TestWithPreconditionAsync(saved.Revision);
        ErrorOr<SteamGridDbSettings> accepted;
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            firstRelease.SetResult();
            accepted = await firstProbe;
        }
        finally
        {
            firstRelease.TrySetResult();
            secondRelease.TrySetResult();
        }
        accepted.IsError.ShouldBeFalse();
        accepted.Value.Revision.ShouldNotBe(saved.Revision);
        (await secondProbe).FirstError.Code.ShouldBe("SteamGridDb.PreconditionFailed");
        (await second.GetAsync()).Revision.ShouldBe(accepted.Value.Revision);
    }

    [Fact]
    public async Task TestConnectionAsync_StalePrecondition_DoesNotCallProvider()
    {
        await using var db = _database.CreateContext();
        var service = Create(db, new() { Before = _ => throw new InvalidOperationException("No provider I/O expected.") });
        (await service.UpdateAsync(new(Guid.Empty, true, "key"))).IsError.ShouldBeFalse();
        (await service.TestWithPreconditionAsync(Guid.Empty)).FirstError.Code.ShouldBe("SteamGridDb.PreconditionFailed");
        (await service.GetAsync()).LastTestedAt.ShouldBeNull();
    }

    public void Dispose() => _database.Dispose();

    private sealed record SettingsUpdate(Guid Revision, bool Enabled, string? ApiKey, bool ClearApiKey = false);

    private sealed class SettingsHarness(SteamGridDbSettingsService service, EfUnitOfWork unitOfWork)
    {
        public Task<SteamGridDbSettings> GetAsync() => service.GetAsync();
        public Task<string?> GetApiKeyAsync() => service.GetApiKeyAsync();
        public Task<ErrorOr<SteamGridDbSettings>> UpdateAsync(SettingsUpdate request) =>
            new ReplaceSteamGridDbSettingsCommandHandler(service, unitOfWork, NullLogger<ReplaceSteamGridDbSettingsCommandHandler>.Instance)
                .HandleAsync(new(new(request.Enabled, request.ApiKey, request.ClearApiKey), RevisionPrecondition.For(request.Revision.ToString())));
        public Task<ErrorOr<SteamGridDbSettings>> TestWithPreconditionAsync(Guid revision, CancellationToken ct = default) =>
            new TestSteamGridDbConnectionCommandHandler(service, unitOfWork, NullLogger<TestSteamGridDbConnectionCommandHandler>.Instance)
                .HandleAsync(new(RevisionPrecondition.For(revision.ToString())), ct);
        public async Task<SteamGridDbSettings> TestConnectionAsync(CancellationToken ct = default) =>
            (await TestWithPreconditionAsync((await GetAsync()).Revision, ct)).Value;
    }

    private sealed class ProbeHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string Body { get; init; } = "{\"success\":true,\"data\":[]}";
        public Func<CancellationToken, Task>? Before { get; init; }
        public string? Uri { get; private set; }
        public string? Authorization { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri?.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            if (Before is not null)
                await Before(ct);
            return new(Status) { Content = new StringContent(Body) };
        }
    }
}
