using Romd.Persistence.MetadataProviders;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Infrastructure.Enrichment;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed class IgdbProviderSettingsServiceTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();

    private IgdbProviderSettingsService CreateService(RomdDbContext db,
        HttpMessageHandler? handler = null, IgdbProviderOptions? deployment = null,
        EnrichmentOptions? enrichment = null, IDataProtectionProvider? protection = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("IgdbConnectionTest").Returns(_ => new HttpClient(handler ?? new ProbeHandler(), false));
        return new IgdbProviderSettingsService(new IgdbProviderSettingsStore(db), protection ?? _protection,
            Options.Create(deployment ?? new()), Options.Create(enrichment ?? new()), factory, TimeProvider.System);
    }

    [Fact]
    public async Task UpdateAsync_StaleRevision_RejectsWithoutOverwritingCredentials()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db);
        var baseline = await service.GetAsync();
        (await service.UpdateAsync(new(true, "client", "secret") { Revision = baseline.Revision })).IsError.ShouldBeFalse();
        var stale = await service.UpdateAsync(new(false, "client", null) { Revision = baseline.Revision });
        stale.IsError.ShouldBeTrue();
        (await service.GetAsync()).Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateAsync_Secret_IsEncryptedWriteOnlyAndReadableWithSharedKeys()
    {
        string keys = Path.Combine(Path.GetTempPath(), $"romd-igdb-keys-{Guid.NewGuid():N}");
        try
        {
            var adminProtection = DataProtectionProvider.Create(new DirectoryInfo(keys), builder => builder.SetApplicationName("romd"));
            await using var adminDb = _database.CreateContext();
            var service = CreateService(adminDb, protection: adminProtection);
            var result = await service.UpdateLatestAsync(new(true, "client-a", "secret-a"));

            result.IsError.ShouldBeFalse();
            result.Value.IsConfigured.ShouldBeTrue();
            result.Value.HasClientSecret.ShouldBeTrue();
            JsonSerializer.Serialize(result.Value).ShouldNotContain("secret-a");
            var stored = await adminDb.Set<MetadataProviderSettingsEntity>().SingleAsync(x => x.ProviderId == "igdb");
            stored.ProtectedClientSecret.ShouldNotBe("secret-a");
            stored.ProtectedClientSecret.ShouldNotBeNull().ShouldNotContain("secret-a");

            var workerProtection = DataProtectionProvider.Create(new DirectoryInfo(keys), builder => builder.SetApplicationName("romd"));
            await using var workerDb = _database.CreateContext();
            var workerSettings = await CreateService(workerDb, protection: workerProtection).GetAsync();
            workerSettings.IsConfigured.ShouldBeTrue();
            workerSettings.ConfigurationError.ShouldBeNull();
        }
        finally
        {
            if (Directory.Exists(keys)) Directory.Delete(keys, true);
        }
    }

    [Fact]
    public async Task UpdateAsync_RetainReplaceRemoveSecret_UsesExplicitSemantics()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db);
        (await service.UpdateLatestAsync(new(true, "client-a", "secret-a"))).IsError.ShouldBeFalse();
        var original = (await db.Set<MetadataProviderSettingsEntity>().SingleAsync(x => x.ProviderId == "igdb")).ProtectedClientSecret;

        (await service.UpdateLatestAsync(new(false, "client-a", null))).IsError.ShouldBeFalse();
        (await db.Set<MetadataProviderSettingsEntity>().SingleAsync(x => x.ProviderId == "igdb")).ProtectedClientSecret.ShouldBe(original);
        (await service.UpdateLatestAsync(new(false, "client-b", null))).IsError.ShouldBeTrue();
        (await service.UpdateLatestAsync(new(true, "client-b", "secret-b"))).IsError.ShouldBeFalse();
        (await db.Set<MetadataProviderSettingsEntity>().SingleAsync(x => x.ProviderId == "igdb")).ProtectedClientSecret.ShouldNotBe(original);
        (await service.UpdateLatestAsync(new(true, "client-b", null, true))).IsError.ShouldBeTrue();
        (await service.UpdateLatestAsync(new(false, "client-b", "secret-c", true))).IsError.ShouldBeTrue();

        var removed = await service.UpdateLatestAsync(new(false, "client-b", null, true));
        removed.IsError.ShouldBeFalse();
        removed.Value.HasClientSecret.ShouldBeFalse();
        removed.Value.IsConfigured.ShouldBeFalse();
        (await db.Set<MetadataProviderSettingsEntity>().SingleAsync(x => x.ProviderId == "igdb")).ProtectedClientSecret.ShouldBeNull();
    }

    [Theory]
    [InlineData("deployment-id", "deployment-secret", true)]
    [InlineData("deployment-id", null, false)]
    [InlineData(null, "deployment-secret", false)]
    public async Task GetAsync_AnyDeploymentCredential_OverridesAndLocksStoredSettings(string? clientId, string? secret, bool configured)
    {
        await using var db = _database.CreateContext();
        (await CreateService(db).UpdateLatestAsync(new(true, "stored-id", "stored-secret"))).IsError.ShouldBeFalse();
        var service = CreateService(db, deployment: new() { ClientId = clientId, ClientSecret = secret });

        var result = await service.GetAsync();
        result.ManagedByDeployment.ShouldBeTrue();
        result.ClientId.ShouldBe(clientId);
        result.IsConfigured.ShouldBe(configured);
        if (!configured) result.ConfigurationError.ShouldNotBeNull().ShouldContain("Deployment");
        (await service.UpdateLatestAsync(new(false, null, null, true))).FirstError.Code.ShouldBe("MetadataProviders.ManagedByDeployment");
    }

    [Fact]
    public async Task GetAsync_RuntimeEnablement_IgnoresLegacyDeploymentDisable()
    {
        await using var db = _database.CreateContext();
        var enrichment = new EnrichmentOptions();
        enrichment.Providers["igdb"].Enabled = false;
        var service = CreateService(db, enrichment: enrichment);
        (await service.UpdateLatestAsync(new(true, "client", "secret"))).Value.Enabled.ShouldBeTrue();
        var managed = CreateService(db, deployment: new() { ClientId = "deployment-id", ClientSecret = "deployment-secret" }, enrichment: enrichment);
        (await managed.GetAsync()).Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAsync_UnreadableSecret_ReportsRepairAndCanReplace()
    {
        await using var db = _database.CreateContext();
        (await CreateService(db).UpdateLatestAsync(new(true, "client", "secret"))).IsError.ShouldBeFalse();
        var service = CreateService(db, protection: new EphemeralDataProtectionProvider());

        var broken = await service.GetAsync();
        broken.HasClientSecret.ShouldBeTrue();
        broken.IsConfigured.ShouldBeFalse();
        broken.ConfigurationError.ShouldNotBeNull().ShouldContain("cannot be decrypted");
        (await service.UpdateLatestAsync(new(true, "client", null))).IsError.ShouldBeTrue();
        (await service.UpdateLatestAsync(new(true, "client", "replacement"))).Value.IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public async Task TestConnectionAsync_ValidCredentials_ChecksTokenAndIgdbThenPersistsResult()
    {
        await using var db = _database.CreateContext();
        var handler = new ProbeHandler();
        var service = CreateService(db, handler);
        (await service.UpdateLatestAsync(new(false, "client", "secret"))).IsError.ShouldBeFalse();

        var tested = await service.TestConnectionAsync();
        tested.LastTestSucceeded.ShouldBe(true);
        tested.LastTestedAt.ShouldNotBeNull();
        handler.RequestUris.ShouldBe(["https://id.twitch.tv/oauth2/token", "https://api.igdb.com/v4/games"]);
        handler.ClientIds.ShouldBe(["client"]);
        handler.AuthorizationTokens.ShouldBe(["token"]);
        (await CreateService(db).GetAsync()).LastTestSucceeded.ShouldBe(true);
        (await service.UpdateLatestAsync(new(true, "client", null))).Value.LastTestedAt.ShouldBeNull();
    }

    [Fact]
    public async Task TestConnectionAsync_NetworkFailure_DoesNotPersistExceptionDetails()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db, new ProbeHandler
        {
            BeforeToken = _ => throw new HttpRequestException("upstream-secret and token")
        });
        (await service.UpdateLatestAsync(new(true, "client", "secret"))).IsError.ShouldBeFalse();

        var result = await service.TestConnectionAsync();
        result.LastTestSucceeded.ShouldBe(false);
        result.LastTestMessage.ShouldNotBeNull().ShouldContain("network connection");
        result.LastTestMessage.ShouldNotBeNull().ShouldNotContain("upstream-secret");
        (await db.Set<MetadataProviderSettingsEntity>().SingleAsync(x => x.ProviderId == "igdb")).LastTestMessage.ShouldBe(result.LastTestMessage);
    }

    [Fact]
    public async Task TestConnectionAsync_CallerCancels_DoesNotPersistMisleadingFailure()
    {
        await using var db = _database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var handler = new ProbeHandler
        {
            BeforeToken = ct =>
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        };
        var service = CreateService(db, handler);
        (await service.UpdateLatestAsync(new(true, "client", "secret"))).IsError.ShouldBeFalse();

        await Should.ThrowAsync<OperationCanceledException>(() => service.TestConnectionAsync(cancellation.Token));
        (await service.GetAsync()).LastTestedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"access_token\":42}")]
    [InlineData("not json with secret")]
    public async Task TestConnectionAsync_MalformedAuthenticationResponse_ReturnsSanitizedFailure(string body)
    {
        await using var db = _database.CreateContext();
        var handler = new ProbeHandler { TokenBody = body };
        var service = CreateService(db, handler);
        (await service.UpdateLatestAsync(new(true, "client", "secret"))).IsError.ShouldBeFalse();

        var result = await service.TestConnectionAsync();
        result.LastTestSucceeded.ShouldBe(false);
        result.LastTestMessage.ShouldNotBeNull().ShouldNotContain("secret");
        handler.RequestUris.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TestConnectionAsync_IgdbRejectsToken_DoesNotReportAuthenticationOnlySuccess()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db, new ProbeHandler { IgdbStatus = HttpStatusCode.Forbidden });
        (await service.UpdateLatestAsync(new(true, "client", "secret"))).IsError.ShouldBeFalse();

        var result = await service.TestConnectionAsync();
        result.LastTestSucceeded.ShouldBe(false);
        result.LastTestMessage.ShouldNotBeNull().ShouldContain("authentication failed");
        result.LastTestMessage.ShouldNotBeNull().ShouldNotContain("upstream-secret");
    }

    [Fact]
    public async Task TestConnectionAsync_ConfigurationChangesDuringProbe_DiscardsOldResult()
    {
        await using var testDb = _database.CreateContext();
        await using var editDb = _database.CreateContext();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ProbeHandler { BeforeToken = async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); } };
        var service = CreateService(testDb, handler);
        (await service.UpdateLatestAsync(new(true, "old-client", "old-secret"))).IsError.ShouldBeFalse();

        var probe = service.TestConnectionAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            (await CreateService(editDb).UpdateLatestAsync(new(true, "new-client", "new-secret"))).IsError.ShouldBeFalse();
        }
        finally
        {
            release.SetResult();
        }
        var result = await probe;
        result.ClientId.ShouldBe("new-client");
        result.LastTestedAt.ShouldBeNull();
        result.LastTestSucceeded.ShouldBeNull();
    }

    [Fact]
    public async Task InitializeAsync_ConfigurationChanges_NewScopeSeesNewPairWhileActiveRunKeepsOriginal()
    {
        var handler = new ProbeHandler { DeriveTokenFromClient = true };
        var aliases = Substitute.For<IPlatformAliasRepository>();
        aliases.GetProviderMappingsByShortNameAsync("igdb", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_protection);
        services.AddRomdAdminCurationServices();
        services.AddScoped(_ => _database.CreateContext());
        services.AddSingleton(aliases);
        services.AddSingleton(new HttpClient(handler, false));
        using var root = services.BuildServiceProvider();
        using var oldScope = root.CreateScope();
        var admin = oldScope.ServiceProvider.GetRequiredService<IIgdbProviderSettingsService>();
        (await admin.UpdateLatestAsync(new(true, "old-client", "old-secret"))).IsError.ShouldBeFalse();
        var oldRun = oldScope.ServiceProvider.GetRequiredService<IMetadataProvider>();
        await oldRun.InitializeAsync();
        oldRun.IsConfigured.ShouldBeTrue();

        using var editScope = root.CreateScope();
        (await editScope.ServiceProvider.GetRequiredService<IIgdbProviderSettingsService>()
            .UpdateLatestAsync(new(true, "new-client", "new-secret"))).IsError.ShouldBeFalse();
        using var newScope = root.CreateScope();
        var newRun = newScope.ServiceProvider.GetRequiredService<IMetadataProvider>();
        await newRun.InitializeAsync();
        await oldRun.InitializeAsync();
        var context = new EnrichmentContext { TitleName = "Test", PlatformShortName = "test", ExistingExternalId = "1" };
        await oldRun.EnrichSingleAsync(context);
        await newRun.EnrichSingleAsync(context);

        handler.TokenForms.Count.ShouldBe(2);
        handler.TokenForms[0].ShouldContain("client_id=old-client");
        handler.TokenForms[0].ShouldContain("client_secret=old-secret");
        handler.TokenForms[1].ShouldContain("client_id=new-client");
        handler.TokenForms[1].ShouldContain("client_secret=new-secret");
        handler.ClientIds.ShouldBe(["old-client", "new-client"]);
        handler.AuthorizationTokens.ShouldBe(["old-client-token", "new-client-token"]);

        (await editScope.ServiceProvider.GetRequiredService<IIgdbProviderSettingsService>()
            .UpdateLatestAsync(new(false, "new-client", null))).IsError.ShouldBeFalse();
        using var disabledScope = root.CreateScope();
        var disabledRun = disabledScope.ServiceProvider.GetRequiredService<IMetadataProvider>();
        await disabledRun.InitializeAsync();
        disabledRun.IsConfigured.ShouldBeFalse();
        (await disabledRun.EnrichSingleAsync(context)).Outcome.ShouldBe(EnrichmentOutcome.Error);
        handler.TokenForms.Count.ShouldBe(2);
        oldRun.IsConfigured.ShouldBeTrue();
    }

    public void Dispose() => _database.Dispose();

    private sealed class ProbeHandler : HttpMessageHandler
    {
        public bool DeriveTokenFromClient { get; init; }
        public string TokenBody { get; init; } = "{\"access_token\":\"token\",\"expires_in\":3600}";
        public HttpStatusCode IgdbStatus { get; init; } = HttpStatusCode.OK;
        public Func<CancellationToken, Task>? BeforeToken { get; init; }
        public List<string> RequestUris { get; } = [];
        public List<string> TokenForms { get; } = [];
        public List<string> ClientIds { get; } = [];
        public List<string?> AuthorizationTokens { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestUris.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.Host == "id.twitch.tv")
            {
                TokenForms.Add(await request.Content!.ReadAsStringAsync(ct));
                if (BeforeToken is not null) await BeforeToken(ct);
                string body = TokenBody;
                if (DeriveTokenFromClient)
                {
                    string clientId = TokenForms[^1].Split('&').Single(x => x.StartsWith("client_id=", StringComparison.Ordinal))[10..];
                    body = JsonSerializer.Serialize(new { access_token = $"{clientId}-token", expires_in = 3600 });
                }
                return new(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
            ClientIds.Add(request.Headers.GetValues("Client-ID").Single());
            AuthorizationTokens.Add(request.Headers.Authorization?.Parameter);
            return new(IgdbStatus) { Content = new StringContent(IgdbStatus == HttpStatusCode.OK ? "[]" : "upstream-secret") };
        }
    }
}

internal static class IgdbSettingsTestUpdates
{
    public static async Task<ErrorOr.ErrorOr<IgdbProviderSettingsDto>> UpdateLatestAsync(
        this IIgdbProviderSettingsService service, UpdateIgdbProviderSettingsRequest request) =>
        await service.UpdateAsync(request with { Revision = (await service.GetAsync()).Revision });
}
