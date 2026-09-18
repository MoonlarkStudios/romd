using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OpenIddict.EntityFrameworkCore;
using Romd.Persistence;
using Romd.Persistence.Search;
using Testcontainers.PostgreSql;
using RomdPostgreSql = Romd.Persistence.PostgreSqlConfiguration;

namespace Romd.PostgreSql.TestSupport;

/// <summary>
///     One isolated PostgreSQL database per test class (decision D1). Each test assembly shares a single
///     <c>postgres:18-bookworm</c> container whose template database is migrated once from the
///     Npgsql baseline; each instance is a <c>CREATE DATABASE ... TEMPLATE</c> copy and is dropped on
///     dispose. Requires Docker: <c>mise run test</c> fails fast with an explanation when it is not
///     running.
/// </summary>
public sealed class PostgreSqlTestDatabase : IDisposable, IAsyncDisposable
{
    private const string TemplateDatabase = "romd_template";
    private static readonly Lazy<PostgreSqlContainer> Server =
        new(StartServer, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly string _databaseName;

    private PostgreSqlTestDatabase(string databaseName, string connectionString)
    {
        _databaseName = databaseName;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    /// <summary>
    ///     Creates a fresh copy of the migrated template. Synchronous because xUnit constructs test
    ///     classes synchronously; the only awaited work is the one-time container start.
    /// </summary>
    public static PostgreSqlTestDatabase Create()
    {
        string databaseName = $"t_{Guid.NewGuid():N}";
        using (var maintenance = OpenMaintenanceConnection())
        using (var command = maintenance.CreateCommand())
        {
            command.CommandText = $"CREATE DATABASE \"{databaseName}\" TEMPLATE {TemplateDatabase}";
            command.ExecuteNonQuery();
        }

        return new PostgreSqlTestDatabase(databaseName, ForDatabase(databaseName));
    }

    public DbContextOptions<RomdDbContext> CreateOptions(
        Action<DbContextOptionsBuilder<RomdDbContext>>? configure = null)
    {
        var builder = new DbContextOptionsBuilder<RomdDbContext>();
        RomdPostgreSql.Configure(builder, ConnectionString);
        builder.UseOpenIddict();
        builder.AddInterceptors(new SearchDocumentInterceptor());
        configure?.Invoke(builder);
        return builder.Options;
    }

    public RomdDbContext CreateContext() => new(CreateOptions());

    /// <summary>
    ///     Runs the worker's schema provisioner against this database (migrations are already
    ///     applied by the template, so this verifies search documents and publishes the schema
    ///     version that API-host readiness requires).
    /// </summary>
    public Task ProvisionAsync(CancellationToken cancellationToken = default)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{RomdPostgreSql.ProvisioningConnectionName}"] = ConnectionString
            })
            .Build();
        return new PostgreSqlSchemaProvisioner(configuration, NullLogger<PostgreSqlSchemaProvisioner>.Instance)
            .StartAsync(cancellationToken);
    }

    public void Dispose()
    {
        NpgsqlConnection.ClearPool(new NpgsqlConnection(ConnectionString));
        using var maintenance = OpenMaintenanceConnection();
        using var command = maintenance.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
        command.ExecuteNonQuery();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static NpgsqlConnection OpenMaintenanceConnection()
    {
        var connection = new NpgsqlConnection(ForDatabase("postgres"));
        connection.Open();
        return connection;
    }

    private static string ForDatabase(string databaseName) =>
        new NpgsqlConnectionStringBuilder(Server.Value.GetConnectionString())
        {
            Database = databaseName,
            Pooling = databaseName != "postgres"
        }.ConnectionString;

    private static PostgreSqlContainer StartServer()
    {
        var container = new PostgreSqlBuilder("postgres:18-bookworm")
            .WithDatabase("romd")
            .WithUsername("romd")
            .WithPassword("romd-test-password")
            .WithEnvironment("POSTGRES_INITDB_ARGS", "--encoding=UTF8 --locale=C")
            .Build();
        try
        {
            container.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Docker is required: the backend test suites run against PostgreSQL in Testcontainers " +
                "(decision D1 of #128). Start Docker (see docs/known-issues.md for Rancher Desktop recovery) "
                + "and retry.",
                exception);
        }

        CreateTemplate(container.GetConnectionString());
        return container;
    }

    private static void CreateTemplate(string bootstrapConnectionString)
    {
        string templateConnectionString = new NpgsqlConnectionStringBuilder(bootstrapConnectionString)
        {
            Database = TemplateDatabase,
            Pooling = false
        }.ConnectionString;
        string maintenanceConnectionString = new NpgsqlConnectionStringBuilder(bootstrapConnectionString)
        {
            Database = "postgres",
            Pooling = false
        }.ConnectionString;

        using (var maintenance = new NpgsqlConnection(maintenanceConnectionString))
        {
            maintenance.Open();
            using var create = maintenance.CreateCommand();
            create.CommandText = $"CREATE DATABASE {TemplateDatabase}";
            create.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<RomdDbContext>();
        RomdPostgreSql.Configure(options, templateConnectionString);
        options.UseOpenIddict();
        using (var context = new RomdDbContext(options.Options))
        {
            context.Database.Migrate();
        }

        using (var template = new NpgsqlConnection(templateConnectionString))
        {
            template.Open();
            using var sync = template.CreateCommand();
            sync.CommandText = IdentityResyncTriggersSql;
            sync.ExecuteNonQuery();
        }

        using (var maintenance = new NpgsqlConnection(maintenanceConnectionString))
        {
            maintenance.Open();
            using var mark = maintenance.CreateCommand();
            mark.CommandText = $"ALTER DATABASE {TemplateDatabase} IS_TEMPLATE true";
            mark.ExecuteNonQuery();
        }
    }

    // Test fixtures seed rows with explicit ids and then let the code under test insert more.
    // SQLite's AUTOINCREMENT continued past explicit ids; PostgreSQL identity sequences do not,
    // so every identity table gets a statement-level trigger that moves its sequence past the
    // ids the statement inserted. The move is forward-only: recomputing MAX over the table would
    // let one of two concurrent inserters rewind the sequence below an id the other already
    // drew, which surfaces as a duplicate-key error. Test-only: production ids are never inserted
    // explicitly outside the migration tool, which reseeds sequences itself.
    private const string IdentityResyncTriggersSql = $$"""
        CREATE FUNCTION {{RomdPostgreSql.SchemaName}}.test_resync_identity() RETURNS trigger
        LANGUAGE plpgsql AS $body$
        DECLARE
            sequence_name text := pg_get_serial_sequence(format('%I.%I', TG_TABLE_SCHEMA, TG_TABLE_NAME), 'Id');
            max_inserted bigint;
        BEGIN
            IF sequence_name IS NOT NULL THEN
                SELECT MAX("Id") INTO max_inserted FROM inserted;
                IF max_inserted IS NOT NULL
                   AND max_inserted > COALESCE(pg_sequence_last_value(sequence_name::regclass), 0) THEN
                    PERFORM setval(sequence_name, max_inserted, true);
                END IF;
            END IF;
            RETURN NULL;
        END
        $body$;

        DO $triggers$
        DECLARE
            identity_table record;
        BEGIN
            FOR identity_table IN
                SELECT table_name
                FROM information_schema.columns
                WHERE table_schema = '{{RomdPostgreSql.SchemaName}}'
                  AND column_name = 'Id'
                  AND is_identity = 'YES'
            LOOP
                EXECUTE format(
                    'CREATE TRIGGER test_resync_identity AFTER INSERT ON %I.%I '
                    || 'REFERENCING NEW TABLE AS inserted '
                    || 'FOR EACH STATEMENT EXECUTE FUNCTION %I.test_resync_identity()',
                    '{{RomdPostgreSql.SchemaName}}', identity_table.table_name, '{{RomdPostgreSql.SchemaName}}');
            END LOOP;
        END
        $triggers$;
        """;
}
