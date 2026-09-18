using DotNet.Testcontainers.Configurations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     PostgreSQL 18 bootstrapped the way compose.yaml does it: the checked-in
///     deploy/postgres/init scripts run at initdb, so tests connect through the real
///     romd_provisioner / romd_worker / romd_admin / romd_consumer roles instead of a superuser.
/// </summary>
internal sealed class RomdPostgreSqlContainer : IAsyncDisposable
{
    private const string BootstrapPassword = "romd-bootstrap-test-password";
    private readonly PostgreSqlContainer _container;

    private RomdPostgreSqlContainer(PostgreSqlContainer container) => _container = container;

    public string BootstrapConnectionString => _container.GetConnectionString();

    /// <summary>Docker container id, for tooling that reaches the server with <c>docker exec</c>.</summary>
    public string ContainerId => _container.Id;
    public string ProvisionerConnectionString => ForRole("romd_provisioner");
    public string WorkerConnectionString => ForRole("romd_worker");
    public string AdminConnectionString => ForRole("romd_admin");
    public string ConsumerConnectionString => ForRole("romd_consumer");

    public IReadOnlyList<string> RuntimeConnectionStrings =>
        [WorkerConnectionString, AdminConnectionString, ConsumerConnectionString];

    public static async Task<RomdPostgreSqlContainer> StartWithRomdRolesAsync()
    {
        PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18-bookworm")
            .WithDatabase("romd")
            .WithUsername("romd_bootstrap")
            .WithPassword(BootstrapPassword)
            .WithEnvironment("POSTGRES_INITDB_ARGS", "--encoding=UTF8 --locale=C")
            .WithEnvironment("ROMD_POSTGRES_PROVISIONER_PASSWORD", RolePassword("romd_provisioner"))
            .WithEnvironment("ROMD_POSTGRES_WORKER_PASSWORD", RolePassword("romd_worker"))
            .WithEnvironment("ROMD_POSTGRES_ADMIN_PASSWORD", RolePassword("romd_admin"))
            .WithEnvironment("ROMD_POSTGRES_CONSUMER_PASSWORD", RolePassword("romd_consumer"))
            .WithResourceMapping(
                new DirectoryInfo(Path.Combine(HangfirePostgreSqlTestSupport.RepositoryRoot, "deploy", "postgres", "init")),
                "/docker-entrypoint-initdb.d/",
                fileMode: Unix.FileMode755)
            .Build();
        await container.StartAsync();
        return new RomdPostgreSqlContainer(container);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    private string ForRole(string role) => new NpgsqlConnectionStringBuilder(BootstrapConnectionString)
    {
        Username = role,
        Password = RolePassword(role)
    }.ConnectionString;

    private static string RolePassword(string role) => $"{role}-test-password";
}
