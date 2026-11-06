using System.Text.Json;
using ErrorOr;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Users;
using Romd.Contracts.Management.Users;
using Romd.Infrastructure.Storage;
using Romd.Hosting;
using Romd.Persistence;
using Romd.Persistence.Identity;
using Romd.Worker.Host;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class AdminRecoveryCommandTests
{
    public static TheoryData<string[]> InvalidArguments => new()
    {
        new string[] { "--recover-admin" },
        new string[] { "--RECOVER-ADMIN", "owner@example.com" },
        new string[] { "--recover-admin=owner@example.com" },
        new string[] { "--recover-admin", "" },
        new string[] { "--recover-admin", " owner@example.com" },
        new string[] { "--recover-admin", "--migrate-database" },
        new string[] { "--recover-admin", "owner@example.com", "--migrate-database" },
        new string[] { "--import-redump-disc", "210", "--recover-admin", "owner@example.com" }
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task RunAsync_InvalidArguments_RefusesBeforeHostConstruction(string[] args)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        int code = await AdminRecoveryCommand.RunAsync(args,
            () => throw new Exception("Must not build a host."), output, errors);
        code.ShouldBe(AdminRecoveryCommand.InvalidArguments);
        output.ToString().ShouldBeEmpty();
        errors.ToString().ShouldContain("Usage:");
        AdminRecoveryCommand.IsRequested(args).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null, true, false)]
    [InlineData("https://admin.example.com", true, true)]
    [InlineData("https://admin.example.com:8443/", true, true)]
    [InlineData("http://admin.example.com", false, false)]
    [InlineData("http://localhost:5137", false, true)]
    [InlineData("http://localhost:5137", true, false)]
    [InlineData("https://user:secret@admin.example.com", true, false)]
    [InlineData("https://admin.example.com/path", true, false)]
    [InlineData("https://admin.example.com/?token=secret", true, false)]
    [InlineData("https://admin.example.com/#fragment", true, false)]
    public void TryGetOrigin_RequiresExactTrustedPortalOrigin(string? origin, bool production, bool expected) =>
        AdminRecoveryCommand.TryGetOrigin(origin, production, out _).ShouldBe(expected);

    [Fact]
    public async Task RunAsync_Failure_OnlyReportsExceptionType()
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        int code = await AdminRecoveryCommand.RunAsync([AdminRecoveryCommand.Flag, "owner@example.com"],
            () => throw new InvalidOperationException("Password=private-test-secret"), output, errors);
        code.ShouldBe(AdminRecoveryCommand.Refused);
        output.ToString().ShouldBeEmpty();
        errors.ToString().ShouldContain(nameof(InvalidOperationException));
        errors.ToString().ShouldNotContain("private-test-secret");
    }

    [Fact]
    public void CreateAdminRecoveryHostBuilder_UsesOnlyRuntimeCredentialsAndStartsNoServices()
    {
        using var host = Builder("Host=localhost;Database=recovery;Username=romd_worker;Password=test-only").Build();
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<RomdDbContext>().Database.GetConnectionString()!.ShouldContain("Username=romd_worker");
        services.GetRequiredService<LocalAdminRecovery>().ShouldNotBeNull();
        services.GetRequiredService<IAccountLifecycle>().ShouldBeOfType<AccountLifecycle>();
        services.GetServices<IHostedService>().ShouldBeEmpty();
        services.GetService<PostgreSqlSchemaProvisioner>().ShouldBeNull();
        services.GetService<ChdDeploymentRuntime>().ShouldBeNull();
    }

    [Fact]
    public async Task RunAsync_MissingRuntimeCredential_RefusesWithoutProvisioningFallback()
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        int code = await AdminRecoveryCommand.RunAsync([AdminRecoveryCommand.Flag, "owner@example.com"],
            () => Builder("unused").ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Romd"] = null })), output, errors);
        code.ShouldBe(AdminRecoveryCommand.Refused);
        output.ToString().ShouldBeEmpty();
        errors.ToString().ShouldNotContain("forbidden-provisioning-fallback");
    }

    [Theory]
    [InlineData("romd_admin", null)]
    [InlineData("romd_provisioner", null)]
    [InlineData("romd_consumer", null)]
    [InlineData("romd_worker", null)]
    [InlineData("romd_worker", -1)]
    public async Task RunCoreAsync_WrongRoleOrSchema_RefusesBeforeIssuingOrOpeningTransaction(string role, int? version)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var schema = Substitute.For<IRomdSchemaProbe>();
        schema.GetPublishedVersionAsync(default).Returns(version);
        var unit = Substitute.For<IUnitOfWork>();
        int code = await AdminRecoveryCommand.RunCoreAsync("owner@example.com", new("https://admin.example.com"),
            schema, unit, true, _ => Task.FromResult(role),
            (_, _) => throw new Exception("Must not issue."), output, errors, default);
        code.ShouldBe(AdminRecoveryCommand.Refused);
        output.ToString().ShouldBeEmpty();
        await unit.DidNotReceiveWithAnyArgs().BeginTransactionAsync();
        if (role != "romd_worker")
            await schema.DidNotReceiveWithAnyArgs().GetPublishedVersionAsync(default);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("refusal")]
    [InlineData("commit-failure")]
    [InlineData("cancellation")]
    public async Task RunCoreAsync_TransactionOutcome_OnlyPublishesCommittedLink(string outcome)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var schema = Substitute.For<IRomdSchemaProbe>();
        schema.GetPublishedVersionAsync(Arg.Any<CancellationToken>()).Returns(PostgreSqlConfiguration.ExpectedSchemaVersion);
        var unit = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        unit.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            output.ToString().ShouldBeEmpty();
            if (outcome == "commit-failure")
                throw new InvalidOperationException("Commit failed.");
            return Task.CompletedTask;
        });
        Task<int> Run() => AdminRecoveryCommand.RunCoreAsync("owner@example.com", new("https://admin.example.com"),
            schema, unit, true, _ => Task.FromResult("romd_worker"), (_, _) =>
            {
                if (outcome == "cancellation")
                    cancellation.Cancel();
                return Task.FromResult<ErrorOr<IssuedAccountLinkDto>>(outcome == "refusal"
                    ? Error.NotFound("Account.AdminNotFound", "No administrator.")
                    : new IssuedAccountLinkDto(Guid.NewGuid(), new string('A', 64), DateTimeOffset.UtcNow.AddHours(1)));
            }, output, errors, cancellation.Token);

        if (outcome == "commit-failure")
            await Should.ThrowAsync<InvalidOperationException>(Run);
        else if (outcome == "cancellation")
            await Should.ThrowAsync<OperationCanceledException>(Run);
        else
            (await Run()).ShouldBe(outcome == "success" ? AdminRecoveryCommand.Success : AdminRecoveryCommand.Refused);
        if (outcome == "success")
        {
            using var json = JsonDocument.Parse(output.ToString());
            json.RootElement.GetProperty("recoveryUrl").GetString().ShouldBe($"https://admin.example.com/activate#{new string('A', 64)}");
        }
        else
            output.ToString().ShouldBeEmpty();
        if (outcome is "refusal" or "cancellation")
            await transaction.DidNotReceiveWithAnyArgs().CommitAsync();
        await transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task RunAsync_RealDatabase_RecoversExistingSoleAdminWithoutStartingWorker()
    {
        await using var postgres = await RomdPostgreSqlContainer.StartWithRomdRolesAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:RomdProvisioning"] = postgres.ProvisionerConnectionString
        }).Build();
        await new PostgreSqlSchemaProvisioner(configuration, NullLogger<PostgreSqlSchemaProvisioner>.Instance).StartAsync(default);
        string runtime = postgres.WorkerConnectionString;
        Guid id;
        using (var host = Builder(runtime).UseEnvironment(Environments.Production).Build())
        {
            using var scope = host.Services.CreateScope();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<RomdIdentityRole>>();
            (await roles.CreateAsync(new("Admin"))).Succeeded.ShouldBeTrue();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<RomdUser>>();
            var user = new RomdUser { UserName = "owner@example.com", Email = "owner@example.com", EmailConfirmed = true };
            (await users.CreateAsync(user, "OriginalPassword123!")).Succeeded.ShouldBeTrue();
            (await users.AddToRoleAsync(user, "Admin")).Succeeded.ShouldBeTrue();
            id = user.Id;
        }
        using (var rejectedOutput = new StringWriter())
        using (var rejectedErrors = new StringWriter())
        {
            (await AdminRecoveryCommand.RunAsync([AdminRecoveryCommand.Flag, "owner@example.com"],
                () => Builder(postgres.AdminConnectionString).UseEnvironment(Environments.Production), rejectedOutput, rejectedErrors))
                .ShouldBe(AdminRecoveryCommand.Refused);
            rejectedOutput.ToString().ShouldBeEmpty();
            rejectedErrors.ToString().ShouldContain("romd_worker");
        }
        using var output = new StringWriter();
        using var errors = new StringWriter();
        int code = await AdminRecoveryCommand.RunAsync([AdminRecoveryCommand.Flag, "owner@example.com"],
            () => Builder(runtime).UseEnvironment(Environments.Production), output, errors);
        code.ShouldBe(AdminRecoveryCommand.Success, errors.ToString());
        using var result = JsonDocument.Parse(output.ToString());
        string token = new Uri(result.RootElement.GetProperty("recoveryUrl").GetString()!).Fragment[1..];
        // Redemption runs through the ordinary HTTP-host Identity composition.
        using var verifyHost = Builder(runtime)
            .ConfigureServices(services => services.AddRomdIdentity()).Build();
        using var verifyScope = verifyHost.Services.CreateScope();
        var services = verifyScope.ServiceProvider;
        var db = services.GetRequiredService<RomdDbContext>();
        await using var transaction = await services.GetRequiredService<IUnitOfWork>().BeginTransactionAsync();
        (await services.GetRequiredService<IAccountLifecycle>().RedeemLinkAsync(token, "RecoveredPassword123!", default))
            .IsError.ShouldBeFalse();
        await transaction.CommitAsync();
        var manager = services.GetRequiredService<UserManager<RomdUser>>();
        (await manager.CheckPasswordAsync((await manager.FindByIdAsync(id.ToString()))!, "RecoveredPassword123!")).ShouldBeTrue();
        (await db.AdminAuditEvents.AnyAsync(item => item.Action == "Local administrator recovery initiated")).ShouldBeTrue();
    }

    private static IHostBuilder Builder(string runtime) => Romd.Worker.Host.Program.CreateAdminRecoveryHostBuilder()
        .UseEnvironment("Testing")
        .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Romd"] = runtime,
            ["ConnectionStrings:RomdProvisioning"] = "forbidden-provisioning-fallback",
            ["Romd:Auth:AdminSpa:PostLogoutRedirectUris:0"] = "https://admin.example.com",
            ["Romd:Native:LibchdrDirectory"] = "/missing/recovery-does-not-load-native-assets"
        }));
}
