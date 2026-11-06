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
    private readonly string[] _ownedConnectionStrings;

    private RomdPostgreSqlContainer(PostgreSqlContainer container)
    {
        _container = container;
        // Published ports can be reused after a previous fixture stops. Its idle pooled
        // connectors must never become the next fixture's first database connection.
        BootstrapConnectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            ApplicationName = $"romd-integration-{Guid.NewGuid():N}"
        }.ConnectionString;
        ProvisionerConnectionString = ForRole("romd_provisioner");
        WorkerConnectionString = ForRole("romd_worker");
        AdminConnectionString = ForRole("romd_admin");
        ConsumerConnectionString = ForRole("romd_consumer");
        _ownedConnectionStrings =
        [BootstrapConnectionString, ProvisionerConnectionString, WorkerConnectionString, AdminConnectionString, ConsumerConnectionString];
    }

    public string BootstrapConnectionString { get; }

    /// <summary>Container id for tooling that reaches the selected runtime's server.</summary>
    public string ContainerId => _container.Id;
    public string ProvisionerConnectionString { get; }
    public string WorkerConnectionString { get; }
    public string AdminConnectionString { get; }
    public string ConsumerConnectionString { get; }

    public IReadOnlyList<string> RuntimeConnectionStrings =>
        [WorkerConnectionString, AdminConnectionString, ConsumerConnectionString];

    public static async Task<RomdPostgreSqlContainer> StartWithRomdRolesAsync(int? hostPort = null)
    {
        PostgreSqlBuilder builder = new PostgreSqlBuilder("postgres:18-bookworm")
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
                fileMode: Unix.FileMode755);
        if (hostPort is { } port)
            builder = builder.WithPortBinding(port, 5432);

        PostgreSqlContainer container = builder.Build();
        try
        {
            await container.StartAsync();
            return new RomdPostgreSqlContainer(container);
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Clear only this fixture's pools while the server is still available. Busy
        // connectors are also marked for closure when their caller returns them.
        foreach (string connectionString in _ownedConnectionStrings)
        {
            using var connection = new NpgsqlConnection(connectionString);
            NpgsqlConnection.ClearPool(connection);
        }

        await _container.DisposeAsync();
    }

    private string ForRole(string role) => new NpgsqlConnectionStringBuilder(BootstrapConnectionString)
    {
        Username = role,
        Password = RolePassword(role)
    }.ConnectionString;

    private static string RolePassword(string role) => $"{role}-test-password";
}
