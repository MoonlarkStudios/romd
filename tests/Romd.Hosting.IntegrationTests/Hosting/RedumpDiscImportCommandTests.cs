using System.Text.Json;
using ErrorOr;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Persistence;
using Romd.Persistence.Repositories;
using Romd.Worker.Host;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <remarks>Fake collaborators qualify command ordering and custody, never live HTTPS, PostgreSQL
/// roles, rollback, migration or publication. Composition tests build but never start a host.</remarks>
public sealed class RedumpDiscImportCommandTests
{
    public static TheoryData<string[]> InvalidArguments => new()
    {
        new string[] {  },
        new string[] { RedumpDiscImportCommand.Flag },
        new string[] { RedumpDiscImportCommand.Flag, "" },
        new string[] { RedumpDiscImportCommand.Flag, "0" },
        new string[] { RedumpDiscImportCommand.Flag, "-1" },
        new string[] { RedumpDiscImportCommand.Flag, "+1" },
        new string[] { RedumpDiscImportCommand.Flag, " 1" },
        new string[] { RedumpDiscImportCommand.Flag, "1 " },
        new string[] { RedumpDiscImportCommand.Flag, "1.0" },
        new string[] { RedumpDiscImportCommand.Flag, "1e2" },
        new string[] { RedumpDiscImportCommand.Flag, "２１０" },
        new string[] { RedumpDiscImportCommand.Flag, "2147483648" },
        new string[] { RedumpDiscImportCommand.Flag, "00000000001" },
        new string[] { RedumpDiscImportCommand.Flag, "210", "--migrate-database" },
        new string[] { "--migrate-database", RedumpDiscImportCommand.Flag, "210" },
        new string[] { RedumpDiscImportCommand.Flag, "210", RedumpDiscImportCommand.Flag, "210" },
        new string[] { "--import-redump-disc=210" },
        new string[] { "--import-redump-disc-other", "210" },
        new string[] { "--IMPORT-REDUMP-DISC", "210" }
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task RunAsync_InvalidArguments_RefusesWithoutBuildingHost(string[] args)
    {
        int builds = 0;
        using var output = new StringWriter();
        using var errors = new StringWriter();

        int result = await RedumpDiscImportCommand.RunAsync(args, () =>
        {
            builds++;
            throw new InvalidOperationException("Host construction is not admission.");
        }, output, errors);

        result.ShouldBe(RedumpDiscImportCommand.InvalidArguments);
        builds.ShouldBe(0);
        output.ToString().ShouldBeEmpty();
        errors.ToString().ShouldContain("Usage:");
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("210", 210)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("0000000001", 1)]
    public void TryParse_BoundedPositiveAsciiDecimal_AcceptsExactDiscId(string value, int expected)
    {
        RedumpDiscImportCommand.TryParse([RedumpDiscImportCommand.Flag, value], out int actual).ShouldBeTrue();
        actual.ShouldBe(expected);
    }

    [Theory]
    [InlineData("--import-redump-disc")]
    [InlineData("--import-redump-disc=210")]
    [InlineData("--import-redump-disc-other")]
    [InlineData("--IMPORT-REDUMP-DISC")]
    public void IsRequested_ImportSyntaxBeforeProvisioningFlags_SelectsStrictImportAdmission(string flag) =>
        RedumpDiscImportCommand.IsRequested(["--migrate-database", flag, "210"]).ShouldBeTrue();

    [Fact]
    public async Task RunAsync_ConfigurationFailure_ReturnsNonzeroWithoutExposingCredentialDetails()
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();

        int result = await RedumpDiscImportCommand.RunAsync([RedumpDiscImportCommand.Flag, "210"],
            () => throw new InvalidOperationException("Password=never-print-test-secret"), output, errors);

        result.ShouldBe(RedumpDiscImportCommand.Refused);
        output.ToString().ShouldBeEmpty();
        errors.ToString().ShouldContain("InvalidOperationException");
        errors.ToString().ShouldNotContain("never-print-test-secret");
    }

    [Fact]
    public void CreateDiscReferenceImportHostBuilder_UsesRuntimeCredentialAndRegistersNoHostedStartup()
    {
        const string runtime = "Host=127.0.0.1;Database=runtime-only;Username=romd_worker;Password=runtime-test";
        using var host = Romd.Worker.Host.Program.CreateDiscReferenceImportHostBuilder()
            .UseEnvironment(Environments.Production)
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Romd"] = runtime,
                    ["ConnectionStrings:RomdProvisioning"] =
                        "Host=127.0.0.1;Database=provisioning-forbidden;Username=romd_provisioner;Password=other-test",
                    ["ConnectionStrings:HangfireProvisioning"] = "intentionally-invalid",
                    ["Romd:Native:LibchdrDirectory"] = "/missing/import-must-not-load-native-assets",
                    ["Romd:Native:ChdmanDirectory"] = "/missing/import-must-not-load-native-tools"
                }))
            .Build();
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<RomdDbContext>();

        context.Database.GetConnectionString().ShouldBe(runtime);
        new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString()!).Username.ShouldBe("romd_worker");
        services.GetRequiredService<IUnitOfWork>().ShouldBeOfType<EfUnitOfWork>();
        services.GetRequiredService<IRomDiscMediaReferenceImportStore>().ShouldBeOfType<RomDiscMediaReferenceImportStore>();
        services.GetRequiredService<IRomdSchemaProbe>().ShouldBeOfType<RomdSchemaProbe>();
        services.GetServices<IHostedService>().ShouldBeEmpty();
        services.GetService<BackgroundJobServer>().ShouldBeNull();
        services.GetService<ChdDeploymentRuntime>().ShouldBeNull();
        services.GetService<PostgreSqlSchemaProvisioner>().ShouldBeNull();
    }

    [Fact]
    public void CreateDiscReferenceImportHostBuilder_MissingRuntimeCredential_DoesNotUseProvisioningFallback()
    {
        using var host = Romd.Worker.Host.Program.CreateDiscReferenceImportHostBuilder()
            .UseEnvironment("Testing")
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Romd"] = null,
                    ["ConnectionStrings:RomdProvisioning"] =
                        "Host=127.0.0.1;Database=forbidden;Username=romd_provisioner"
                }))
            .Build();
        using var scope = host.Services.CreateScope();

        Should.Throw<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<RomdDbContext>())
            .Message.ShouldContain("ConnectionStrings:Romd must be configured");
    }

    [Theory]
    [InlineData("romd")]
    [InlineData("romd_admin")]
    [InlineData("romd_consumer")]
    [InlineData("romd_provisioner")]
    [InlineData("romd_owner")]
    [InlineData("ROMD_WORKER")]
    public async Task RunCoreAsync_ProductionNonWorkerRole_RefusesBeforeSchemaFetchOrTransaction(string role)
    {
        using var session = new Session { Role = role };

        (await session.RunAsync()).ShouldBe(RedumpDiscImportCommand.Refused);

        session.Calls.ShouldBe(["role"]);
        session.ImportsCreated.ShouldBe(0);
        session.Unit.BeginCalls.ShouldBe(0);
        session.Output.ToString().ShouldBeEmpty();
        session.Errors.ToString().ShouldContain("romd_worker");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task RunCoreAsync_UnpublishedOrMismatchedSchema_RefusesBeforeFetchOrTransaction(int? version)
    {
        using var session = new Session();
        session.Schema.Version = version;

        (await session.RunAsync()).ShouldBe(RedumpDiscImportCommand.Refused);

        session.Calls.ShouldBe(["role", "schema"]);
        session.ImportsCreated.ShouldBe(0);
        session.Unit.BeginCalls.ShouldBe(0);
        session.Output.ToString().ShouldBeEmpty();
        session.Errors.ToString().ShouldContain("worker-published application schema");
    }

    [Fact]
    public async Task RunCoreAsync_PreparedExactRecord_CommitsOnceAndReturnsBoundFactsWithoutBody()
    {
        using var session = new Session();

        (await session.RunAsync()).ShouldBe(RedumpDiscImportCommand.Success);

        session.Calls.ShouldBe(["role", "schema", "create", "prepare", "begin", "stage", "commit", "dispose-transaction", "dispose-import"]);
        session.Import.DiscId.ShouldBe(210);
        session.Import.PreparedWithoutTransaction.ShouldBeTrue();
        session.Import.StagedWithTransaction.ShouldBeTrue();
        session.Unit.Transaction.Commits.ShouldBe(1);
        session.Unit.Transaction.Rollbacks.ShouldBe(0);
        session.Errors.ToString().ShouldBeEmpty();
        using var json = JsonDocument.Parse(session.Output.ToString());
        var summary = json.RootElement;
        summary.EnumerateObject().Select(property => property.Name).Order().ShouldBe(
            new[] { "recordId", "rawSha256", "rawBytes", "retrievedAtUtc", "source", "system", "media", "fileCount", "logicalBytes", "sha1", "md5", "crc32" }.Order());
        summary.GetProperty("recordId").GetGuid().ShouldBe(session.Record.Id);
        summary.GetProperty("rawSha256").GetString().ShouldBe(session.Record.RawReferenceSha256.ToString());
        summary.GetProperty("rawBytes").GetInt64().ShouldBe(session.Record.RawReferenceBytes);
        DateTimeOffset.Parse(summary.GetProperty("retrievedAtUtc").GetString()!).ShouldBe(session.Record.RetrievedAt);
        summary.GetProperty("source").GetString().ShouldBe(session.Record.ReferenceSource);
        summary.GetProperty("system").GetString().ShouldBe("PlayStation2");
        summary.GetProperty("media").GetString().ShouldBe("Dvd5");
        summary.GetProperty("fileCount").GetInt32().ShouldBe(1);
        summary.GetProperty("logicalBytes").GetInt64().ShouldBe(session.Record.LogicalBytes);
        summary.GetProperty("sha1").GetString().ShouldBe(session.Record.Sha1.ToString());
        summary.GetProperty("md5").GetString().ShouldBe(session.Record.Md5.ToString());
        summary.GetProperty("crc32").GetString().ShouldBe("00000000");
    }

    [Fact]
    public async Task RunCoreAsync_DevelopmentCredential_PreservesExistingRomdRole()
    {
        using var session = new Session { Role = "romd", RequireWorkerRole = false };

        (await session.RunAsync()).ShouldBe(RedumpDiscImportCommand.Success);

        session.Unit.Transaction.Commits.ShouldBe(1);
        session.Calls[0].ShouldBe("role");
    }

    [Fact]
    public async Task RunCoreAsync_PreparationRefused_DoesNotOpenTransactionOrStage()
    {
        using var session = new Session();
        session.Import.PreparationError = Error.Validation("Reference.SourceRefused", "The source was refused.");

        (await session.RunAsync()).ShouldBe(RedumpDiscImportCommand.Refused);

        session.Calls.ShouldBe(["role", "schema", "create", "prepare", "dispose-import"]);
        session.Unit.BeginCalls.ShouldBe(0);
        session.Errors.ToString().ShouldContain("Reference.SourceRefused");
        session.Output.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task RunCoreAsync_StageBusinessError_DisposesUncommittedTransactionAndKeepsPreparedIdentity()
    {
        using var session = new Session();
        session.Import.StagingError = RomDiscMediaReferenceImportErrors.ConflictingIdentity();

        (await session.RunAsync()).ShouldBe(RedumpDiscImportCommand.Refused);

        session.Unit.Transaction.Commits.ShouldBe(0);
        session.Unit.Transaction.Rollbacks.ShouldBe(1);
        session.Import.Record.ShouldBeSameAs(session.Record);
        session.Calls.ShouldBe(["role", "schema", "create", "prepare", "begin", "stage", "dispose-transaction", "dispose-import"]);
        session.Output.ToString().ShouldBeEmpty();
        session.Errors.ToString().ShouldContain("Rom.DiscMediaReferenceImportConflict");
    }

    [Fact]
    public async Task RunCoreAsync_ForeignStagedIdentity_RefusesWithoutCommit()
    {
        using var session = new Session();
        session.Import.StagedRecord = NewRecord(Guid.Parse("22222222-2222-3333-4444-555555555555"));

        (await session.RunAsync()).ShouldBe(RedumpDiscImportCommand.Refused);

        session.Unit.Transaction.Commits.ShouldBe(0);
        session.Unit.Transaction.Rollbacks.ShouldBe(1);
        session.Errors.ToString().ShouldContain("Rom.DiscMediaReferenceImportResult");
        session.Output.ToString().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("role")]
    [InlineData("schema")]
    [InlineData("prepare")]
    [InlineData("begin")]
    [InlineData("stage")]
    [InlineData("commit")]
    public async Task RunCoreAsync_OriginalDependencyFault_PropagatesAndDisposesOwnedCustody(string phase)
    {
        using var session = new Session { FailurePhase = phase };
        var expected = new InvalidOperationException("synthetic private dependency fault");
        session.Failure = expected;
        Exception? observed = null;

        try
        { await session.RunAsync(); }
        catch (Exception failure) { observed = failure; }

        observed.ShouldBeSameAs(expected);
        session.Output.ToString().ShouldBeEmpty();
        session.Unit.Transaction.Disposals.ShouldBe(phase is "stage" or "commit" ? 1 : 0);
        session.Unit.Transaction.Rollbacks.ShouldBe(phase is "stage" or "commit" ? 1 : 0);
        session.Import.Disposals.ShouldBe(phase is "prepare" or "begin" or "stage" or "commit" ? 1 : 0);
    }

    [Theory]
    [InlineData("initial")]
    [InlineData("role")]
    [InlineData("schema")]
    [InlineData("prepare")]
    [InlineData("begin")]
    [InlineData("stage")]
    [InlineData("commit")]
    public async Task RunCoreAsync_CallerCancellation_PreservesTokenAndRollsBackUncommittedCustody(string phase)
    {
        using var session = new Session { CancellationPhase = phase };
        if (phase == "initial")
            session.Cancellation.Cancel();
        OperationCanceledException? observed = null;

        try
        { await session.RunAsync(); }
        catch (OperationCanceledException failure) { observed = failure; }

        observed.ShouldNotBeNull();
        observed.CancellationToken.ShouldBe(session.Cancellation.Token);
        session.Output.ToString().ShouldBeEmpty();
        session.Unit.Transaction.Commits.ShouldBe(0);
        session.Unit.Transaction.Disposals.ShouldBe(phase is "begin" or "stage" or "commit" ? 1 : 0);
        session.Unit.Transaction.Rollbacks.ShouldBe(phase is "begin" or "stage" or "commit" ? 1 : 0);
        session.Import.Disposals.ShouldBe(phase is "prepare" or "begin" or "stage" or "commit" ? 1 : 0);
        if (phase == "initial")
            session.Calls.ShouldBeEmpty();
    }

    private static RomDiscMediaReferenceRecord NewRecord(Guid identity) => new(identity,
        RomDiscReferenceSystem.PlayStation2, RomDiscReferenceMedia.Dvd5, 1, 1048576,
        Sha1.Parse(new string('1', 40)), Md5.Parse(new string('2', 32)), Crc32.FromUInt32(0),
        123, Sha256.Parse(new string('3', 64)), "https://redump.info/disc/210",
        "redump-disc-page/v1", "redump-canonical-https-disc/v1", DateTimeOffset.UnixEpoch.AddTicks(1234));

    private sealed class Session : IDisposable
    {
        public Session()
        {
            Schema = new SchemaProbe(this);
            Unit = new UnitOfWork(this);
            Import = new ImportOperation(this);
        }

        public List<string> Calls { get; } = [];
        public StringWriter Output { get; } = new();
        public StringWriter Errors { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public RomDiscMediaReferenceRecord Record { get; } = NewRecord(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        public SchemaProbe Schema { get; }
        public UnitOfWork Unit { get; }
        public ImportOperation Import { get; }
        public string Role { get; init; } = "romd_worker";
        public bool RequireWorkerRole { get; init; } = true;
        public string? FailurePhase { get; init; }
        public Exception? Failure { get; set; }
        public string? CancellationPhase { get; init; }
        public int ImportsCreated { get; private set; }

        public Task<int> RunAsync() => RedumpDiscImportCommand.RunCoreAsync(210, Schema, Unit,
            RequireWorkerRole, ct =>
            {
                Observe("role", ct);
                return Task.FromResult(Role);
            }, () =>
            {
                Calls.Add("create");
                ImportsCreated++;
                return Import;
            }, Output, Errors, Cancellation.Token);

        public void Observe(string phase, CancellationToken ct)
        {
            Calls.Add(phase);
            ct.ShouldBe(Cancellation.Token);
            if (CancellationPhase == phase)
                Cancellation.Cancel();
            if (FailurePhase == phase)
                throw Failure!;
        }

        public void Dispose()
        {
            Output.Dispose();
            Errors.Dispose();
            Cancellation.Dispose();
        }
    }

    private sealed class SchemaProbe(Session session) : IRomdSchemaProbe
    {
        public int? Version { get; set; } = PostgreSqlConfiguration.ExpectedSchemaVersion;
        public Task<int?> GetPublishedVersionAsync(CancellationToken ct)
        {
            session.Observe("schema", ct);
            return Task.FromResult(Version);
        }
    }

    private sealed class UnitOfWork(Session session) : IUnitOfWork
    {
        public FakeTransaction Transaction { get; } = new(session);
        public int BeginCalls { get; private set; }
        public bool Active { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => TransactionConflict.None;

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The command must not issue a separate flush.");

        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            BeginCalls++;
            session.Observe("begin", cancellationToken);
            Active = true;
            return Task.FromResult<ITransaction>(Transaction);
        }

        public sealed class FakeTransaction(Session session) : ITransaction
        {
            public int Commits { get; private set; }
            public int Rollbacks { get; private set; }
            public int Disposals { get; private set; }
            public Task CommitAsync(CancellationToken cancellationToken = default)
            {
                session.Observe("commit", cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Commits++;
                session.Unit.Active = false;
                return Task.CompletedTask;
            }

            public Task RollbackAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ShouldBe(CancellationToken.None);
                Rollbacks++;
                session.Unit.Active = false;
                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync()
            {
                session.Calls.Add("dispose-transaction");
                Disposals++;
                if (Commits == 0)
                    await RollbackAsync(CancellationToken.None);
            }
        }
    }

    private sealed class ImportOperation(Session session) : RedumpDiscImportCommand.IImportOperation
    {
        public RomDiscMediaReferenceRecord Record => session.Record;
        public Error? PreparationError { get; set; }
        public Error? StagingError { get; set; }
        public RomDiscMediaReferenceRecord? StagedRecord { get; set; }
        public int DiscId { get; private set; }
        public bool PreparedWithoutTransaction { get; private set; }
        public bool StagedWithTransaction { get; private set; }
        public int Disposals { get; private set; }

        public Task<ErrorOr<RomDiscMediaReferenceRecord>> PrepareAsync(int discId, CancellationToken ct)
        {
            DiscId = discId;
            PreparedWithoutTransaction = !session.Unit.Active;
            session.Observe("prepare", ct);
            return Task.FromResult(PreparationError is { } error
                ? ErrorOrFactory.From<RomDiscMediaReferenceRecord>(error) : ErrorOrFactory.From(Record));
        }

        public Task<ErrorOr<RomDiscMediaReferenceRecord>> StageAsync(CancellationToken ct)
        {
            StagedWithTransaction = session.Unit.Active;
            session.Observe("stage", ct);
            return Task.FromResult(StagingError is { } error
                ? ErrorOrFactory.From<RomDiscMediaReferenceRecord>(error) : ErrorOrFactory.From(StagedRecord ?? Record));
        }

        public void Dispose()
        {
            session.Calls.Add("dispose-import");
            Disposals++;
        }
    }
}
