using Romd.Hosting.Vfs;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Vfs;

public sealed class VfsConnectionExportTests
{
    [Fact]
    public void Obscure_KnownStockRcloneVector_MatchesUtf8AndCtrEncoding()
    {
        // Generated independently by stock rclone 1.75.1: rclone obscure 'romd-test-vector-✓'.
        byte[] iv = Convert.FromHexString("8f601a660f9aa9d7f68bbeae8cadb6c6");
        VfsConnectionExport.Obscure("romd-test-vector-✓", iv)
            .ShouldBe("j2AaZg-aqdf2i76ujK22xvYOuxJk6t0QhirTiEevLmwwBrrX");
    }
}
