using Shouldly;
using Xunit;
using static Romd.Hosting.IntegrationTests.Infrastructure.RepositoryPaths;

namespace Romd.Hosting.IntegrationTests.Hosting;

// Runtime Compose merging and negative topology cases are exercised by
// scripts/deployment/test_contract.py; CI also launches the actual images.
public sealed class ProductionOverlayTests
{
    [Fact]
    public void ProductionCompose_UsesReadinessAndWorkerOnlyProvisioning()
    {
        string compose = File.ReadAllText(FindRepositoryFile("compose.yaml"));
        compose.ShouldContain("http://localhost:8080/health/ready");
        compose.ShouldContain("test -f /run/romd/ready");
        compose.ShouldContain("condition: service_healthy");
        compose.ShouldContain("/run/romd:mode=1777");
        compose.ShouldNotContain("container_name:");
        compose.ShouldNotContain("build:");
        compose.ShouldNotContain("192.168.");
        compose.ShouldNotContain("cloudflared:");
        compose.ShouldContain("max-size:");
        compose.ShouldContain("stop_grace_period:");
    }
}
