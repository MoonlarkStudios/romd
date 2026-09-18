using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class DockerPackagingTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Dockerfile_DefinesSplitHostAndIsolatedPlayerTargets()
    {
        string dockerfile = ReadRepositoryFile("Dockerfile");

        dockerfile.ShouldContain("AS romd-admin");
        dockerfile.ShouldContain("AS romd-consumer");
        dockerfile.ShouldContain("AS romd-worker");
        dockerfile.ShouldContain("AS romd-admin-dev");
        dockerfile.ShouldContain("AS romd-consumer-dev");
        dockerfile.ShouldContain("AS romd-player");
        dockerfile.ShouldContain("dotnet publish src/Romd.Admin.Host/Romd.Admin.Host.csproj");
        dockerfile.ShouldContain("dotnet publish src/Romd.Consumer.Host/Romd.Consumer.Host.csproj");
        dockerfile.ShouldContain("dotnet publish src/Romd.Worker.Host/Romd.Worker.Host.csproj");
        dockerfile.ShouldContain("COPY --from=frontend-build /web/packages/romd-admin-app/dist/ ./wwwroot/");
        dockerfile.ShouldContain("COPY --from=frontend-build /web/packages/romd-consumer-app/dist/ ./wwwroot/");
        dockerfile.ShouldContain("COPY --from=player-build /web/packages/romd-player-app/dist/ /usr/share/nginx/html/");
        dockerfile.ShouldContain("deploy/player/entrypoint.sh /docker-entrypoint.d/10-romd-player-config.sh");
        dockerfile.ShouldContain("deploy/player/default.conf.template /etc/romd/default.conf.template");
        dockerfile.ShouldContain("""ENTRYPOINT ["dotnet", "romd-admin.dll"]""");
        dockerfile.ShouldContain("""ENTRYPOINT ["dotnet", "romd-consumer.dll"]""");
        dockerfile.ShouldContain("""ENTRYPOINT ["dotnet", "romd-worker.dll"]""");
        dockerfile.ShouldNotContain("libarchive-tools");
        dockerfile.ShouldNotContain("EMULATORJS_CORES");
        dockerfile.ShouldNotContain("tools/emulatorjs/fetch.mjs");
    }

    [Fact]
    public void Dockerfile_InstallsEveryWebWorkspaceBeforeBuilding()
    {
        string dockerfile = ReadRepositoryFile("Dockerfile");
        string dependencyStage = dockerfile[..dockerfile.IndexOf("RUN pnpm install", StringComparison.Ordinal)];
        foreach (string package in Directory.EnumerateDirectories(Path.Combine(RepositoryRoot, "web", "packages")))
        {
            string manifest = Path.Combine(package, "package.json");
            if (File.Exists(manifest))
                dependencyStage.ShouldContain(Path.GetRelativePath(RepositoryRoot, manifest).Replace('\\', '/'));
        }
    }

    [Fact]
    public void Compose_DefinesSplitHostServicesAndKeepsWorkerPrivate()
    {
        string compose = ReadRepositoryFile("compose.yaml");
        string worker = ExtractServiceBlock(compose, "romd-worker");
        string admin = ExtractServiceBlock(compose, "romd-admin");
        string consumer = ExtractServiceBlock(compose, "romd-consumer");
        string player = ExtractServiceBlock(compose, "romd-player");

        worker.ShouldContain("ROMD_WORKER_IMAGE");
        admin.ShouldContain("ROMD_ADMIN_IMAGE");
        consumer.ShouldContain("ROMD_CONSUMER_IMAGE");
        player.ShouldContain("ROMD_PLAYER_IMAGE");

        worker.ShouldNotContain("ports:");
        admin.ShouldNotContain("ports:");
        consumer.ShouldNotContain("ports:");
        player.ShouldNotContain("ports:");

        worker.ShouldContain("romd-data:/var/lib/romd");
        admin.ShouldContain("romd-data:/var/lib/romd");
        consumer.ShouldContain("romd-data:/var/lib/romd");
        player.ShouldNotContain("romd-data:/var/lib/romd");
        player.ShouldNotContain("depends_on:");
        player.ShouldNotContain("Romd__JwtSecret");
        player.ShouldNotContain("Romd__DataDirectory");

        consumer.ShouldContain("Romd__BrowserPlayback__PlayerOrigins__0__Parent");
        consumer.ShouldContain("Romd__BrowserPlayback__PlayerOrigins__0__Player");
        player.ShouldContain("ROMD_PLAYER_ALLOWED_PARENTS");

        compose.ShouldContain("Romd__DataDirectory: /var/lib/romd");
        compose.ShouldContain("Romd__JwtSecret: ${ROMD_JWT_SECRET:?set ROMD_JWT_SECRET}");
        compose.ShouldContain("Romd__DefaultAdminPassword: ${ROMD_DEFAULT_ADMIN_PASSWORD:?set ROMD_DEFAULT_ADMIN_PASSWORD}");
        compose.ShouldContain("Romd__ConsumerDelivery__SigningSecret: ${ROMD_CONSUMER_DELIVERY_SIGNING_SECRET:?set ROMD_CONSUMER_DELIVERY_SIGNING_SECRET}");
    }

    [Theory]
    [InlineData("compose.yaml")]
    [InlineData("compose.dev.yaml")]
    public void Compose_IgdbDeploymentCredentialsAreLimitedToAdminAndWorker(string path)
    {
        string compose = ReadRepositoryFile(path);
        string commonConfiguration = compose[..compose.IndexOf("\nservices:", StringComparison.Ordinal)];

        commonConfiguration.ShouldNotContain("Providers__Igdb__");
        ExtractServiceBlock(compose, "romd-consumer").ShouldNotContain("Providers__Igdb__");
        foreach (string service in new[] { "romd-admin", "romd-worker" })
        {
            string block = ExtractServiceBlock(compose, service);
            block.ShouldContain("Providers__Igdb__ClientId: ${IGDB_CLIENT_ID:-}");
            block.ShouldContain("Providers__Igdb__ClientSecret: ${IGDB_CLIENT_SECRET:-}");
        }
    }

    [Fact]
    public void DevCompose_DefinesBackendOnlyDailyWorkflowWithViteCors()
    {
        string compose = ReadRepositoryFile("compose.dev.yaml");
        string worker = ExtractServiceBlock(compose, "romd-worker");
        string admin = ExtractServiceBlock(compose, "romd-admin");
        string consumer = ExtractServiceBlock(compose, "romd-consumer");
        string player = ExtractServiceBlock(compose, "romd-player");
        string postgres = ExtractServiceBlock(compose, "postgres");

        worker.ShouldContain("target: romd-worker");
        admin.ShouldContain("target: romd-admin-dev");
        consumer.ShouldContain("target: romd-consumer-dev");
        player.ShouldContain("target: romd-player");

        worker.ShouldNotContain("ports:");
        admin.ShouldContain("""${ROMD_DEV_ADMIN_PORT:-11337}:8080""");
        consumer.ShouldContain("""${ROMD_DEV_CONSUMER_PORT:-11338}:8080""");
        player.ShouldContain("""${ROMD_DEV_PLAYER_PORT:-5175}:8080""");

        worker.ShouldContain("""${ROMD_DEV_DATA_MOUNT:-romd-dev-data}:/var/lib/romd""");
        admin.ShouldContain("""${ROMD_DEV_DATA_MOUNT:-romd-dev-data}:/var/lib/romd""");
        consumer.ShouldContain("""${ROMD_DEV_DATA_MOUNT:-romd-dev-data}:/var/lib/romd""");
        player.ShouldNotContain("/var/lib/romd");
        player.ShouldNotContain("depends_on:");

        postgres.ShouldContain("image: postgres:18-bookworm");
        postgres.ShouldContain("POSTGRES_HOST_AUTH_METHOD: trust");
        postgres.ShouldContain("romd-dev-postgres:/var/lib/postgresql");
        postgres.ShouldNotContain("ports:");
        compose.ShouldContain("ConnectionStrings__Hangfire: Host=postgres;Port=5432;Database=romd;Username=romd");
        worker.ShouldContain("ConnectionStrings__HangfireProvisioning: Host=postgres;Port=5432;Database=romd;Username=romd");
        admin.ShouldNotContain("ConnectionStrings__HangfireProvisioning");
        consumer.ShouldNotContain("ConnectionStrings__HangfireProvisioning");
        player.ShouldNotContain("ConnectionStrings__");

        consumer.ShouldContain("Romd__BrowserPlayback__PlayerOrigins__0__Parent: http://localhost:5174");
        consumer.ShouldContain("Romd__BrowserPlayback__PlayerOrigins__0__Player: ${ROMD_DEV_PLAYER_URL:-http://localhost:5175}");
        player.ShouldContain("ROMD_PLAYER_ALLOWED_PARENTS: ${ROMD_DEV_PLAYER_ALLOWED_PARENTS:-http://localhost:5174}");

        compose.ShouldContain("DOTNET_ENVIRONMENT: Development");
        compose.ShouldContain("ASPNETCORE_ENVIRONMENT: Development");
        compose.ShouldContain("Romd__AdminHost__CorsOrigins__0: http://localhost:5137");
        compose.ShouldContain("Romd__ConsumerHost__CorsOrigins__0: http://localhost:5174");
        compose.ShouldContain("Romd__JwtSecret: ${ROMD_JWT_SECRET:-local-dev-jwt-secret-at-least-32-characters}");
        compose.ShouldContain("Romd__DefaultAdminEmail: ${ROMD_DEFAULT_ADMIN_EMAIL:-admin@localhost}");
        compose.ShouldContain("Romd__DefaultAdminPassword: ${ROMD_DEFAULT_ADMIN_PASSWORD:-LocalDev123}");
        compose.ShouldNotContain("ROMD_JWT_SECRET:?set ROMD_JWT_SECRET");
        compose.ShouldNotContain("ROMD_DEFAULT_ADMIN_PASSWORD:?set ROMD_DEFAULT_ADMIN_PASSWORD");
        compose.ShouldNotContain("ROMD_CONSUMER_DELIVERY_SIGNING_SECRET:?set ROMD_CONSUMER_DELIVERY_SIGNING_SECRET");
    }

    [Fact]
    public void EnvExample_ProvidesRequiredComposeVariablesWithoutRealSecrets()
    {
        string envExample = ReadRepositoryFile(".env.example");

        envExample.ShouldContain("ROMD_JWT_SECRET=");
        envExample.ShouldContain("ROMD_DEFAULT_ADMIN_EMAIL=");
        envExample.ShouldContain("ROMD_DEFAULT_ADMIN_PASSWORD=");
        envExample.ShouldContain("ROMD_CONSUMER_DELIVERY_SIGNING_KEY_ID=");
        envExample.ShouldContain("ROMD_CONSUMER_DELIVERY_SIGNING_SECRET=");
        envExample.ShouldContain("ROMD_ADMIN_PORT=5000");
        envExample.ShouldContain("ROMD_CONSUMER_PORT=5002");
        envExample.ShouldContain("ROMD_PLAYER_PORT=5175");
        envExample.ShouldContain("ROMD_PLAYER_EMULATORJS_SOURCE=cdn");
        envExample.ShouldContain("ROMD_PLAYER_ALLOWED_PARENTS=http://localhost:5002");
        envExample.ShouldNotContain("ChangeMe123!");
        envExample.ShouldNotContain("DefaultDevSecretKey_ChangeInProduction_32chars!");
    }

    [Fact]
    public void PlayerEntrypoint_RendersExactRuntimeShapeAndSecurityHeaders()
    {
        string entrypoint = ReadRepositoryFile("deploy/player/entrypoint.sh");
        string nginx = ReadRepositoryFile("deploy/player/default.conf.template");

        entrypoint.ShouldContain("{\\\"schemaVersion\\\":1,\\\"protocol\\\":1,\\\"emulatorJs\\\"");
        entrypoint.ShouldContain("ROMD_PLAYER_EMULATORJS_SOURCE must be 'cdn'");
        entrypoint.ShouldNotContain("/vendor/emulatorjs/");
        entrypoint.ShouldContain("https://cdn.emulatorjs.org/${ROMD_PLAYER_PIN_VERSION}/data/");
        entrypoint.ShouldContain("the official EmulatorJS CDN data path must use the pinned version");
        entrypoint.ShouldContain("https://cdn.emulatorjs.org:*/*");
        entrypoint.ShouldContain("EmulatorJS core is not in the image pin");
        entrypoint.ShouldContain("invalid allowed parent origin");
        entrypoint.ShouldContain("contains_control_or_header_chars");
        entrypoint.ShouldContain("envsubst '${ROMD_PLAYER_CSP}'");
        entrypoint.ShouldContain("blob: 'wasm-unsafe-eval' 'unsafe-eval'");
        entrypoint.ShouldNotContain("/latest/");
        entrypoint.ShouldNotContain("/stable/");

        nginx.ShouldContain("Content-Security-Policy \"${ROMD_PLAYER_CSP}\" always;");
        nginx.ShouldContain("screen-wake-lock=(self)");
        nginx.ShouldContain("location = /player-config.json");
        nginx.ShouldContain("Access-Control-Allow-Origin \"*\" always;");
        nginx.IndexOf("Access-Control-Allow-Origin", StringComparison.Ordinal)
            .ShouldBe(nginx.LastIndexOf("Access-Control-Allow-Origin", StringComparison.Ordinal));
    }

    [Fact]
    public void Dockerignore_ExcludesLocalBuildDataAndSecrets()
    {
        string dockerignore = ReadRepositoryFile(".dockerignore");

        dockerignore.ShouldContain(".git/");
        dockerignore.ShouldContain("bin/");
        dockerignore.ShouldContain("obj/");
        dockerignore.ShouldContain(".data/");
        dockerignore.ShouldContain("data/");
        dockerignore.ShouldContain("src/Romd.Admin.Host/openapi/");
        dockerignore.ShouldContain("src/Romd.Admin.Host/wwwroot/");
        dockerignore.ShouldContain("src/Romd.Consumer.Host/openapi/");
        dockerignore.ShouldContain("src/Romd.Consumer.Host/wwwroot/");
        dockerignore.ShouldContain("web/node_modules/");
        dockerignore.ShouldContain("web/**/dist/");
        dockerignore.ShouldContain("*.db");
        dockerignore.ShouldContain("*.db-wal");
        dockerignore.ShouldContain(".env");
        dockerignore.ShouldContain("!.env.example");
    }

    private static string ExtractServiceBlock(string compose, string serviceName)
    {
        string[] lines = compose.Split('\n');
        string serviceHeader = $"  {serviceName}:";
        int start = Array.FindIndex(lines, line => line.TrimEnd() == serviceHeader);
        start.ShouldBeGreaterThanOrEqualTo(0);

        int end = lines.Length;
        for (int index = start + 1; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.StartsWith("  ", StringComparison.Ordinal)
                && !line.StartsWith("    ", StringComparison.Ordinal)
                && line.TrimEnd().EndsWith(':'))
            {
                end = index;
                break;
            }

            if (line.Length > 0 && !line.StartsWith(' '))
            {
                end = index;
                break;
            }
        }

        return string.Join('\n', lines[start..end]);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, relativePath));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Romd.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing Romd.sln.");
    }
}
