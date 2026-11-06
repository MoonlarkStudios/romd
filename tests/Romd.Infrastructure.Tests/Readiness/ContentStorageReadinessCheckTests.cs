using Microsoft.Extensions.Options;
using Romd.Application.Common.Readiness;
using Romd.Infrastructure.Readiness;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Readiness;

public sealed class ContentStorageReadinessCheckTests
{
    [Fact]
    public async Task EvaluateAsync_MissingRootDirectory_CreatesItAndReturnsHealthy()
    {
        string parent = Path.Combine(Path.GetTempPath(), $"romd-cas-readiness-{Guid.NewGuid():N}");
        string rootPath = Path.Combine(parent, "content");
        try
        {
            var check = CreateCheck(rootPath);

            var status = await check.EvaluateAsync(CancellationToken.None);

            status.ShouldBe(ReadinessCheckStatus.Healthy);
            Directory.Exists(rootPath).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EvaluateAsync_UncreatableRootPath_ReturnsDegraded()
    {
        string blockingFile = Path.Combine(Path.GetTempPath(), $"romd-cas-readiness-{Guid.NewGuid():N}.blocker");
        await File.WriteAllTextAsync(blockingFile, "not a directory");
        try
        {
            var check = CreateCheck(Path.Combine(blockingFile, "content"));

            var status = await check.EvaluateAsync(CancellationToken.None);

            status.ShouldBe(ReadinessCheckStatus.Degraded);
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    [Fact]
    public async Task EvaluateAsync_WhitespaceRootPath_ReturnsDegraded()
    {
        var check = CreateCheck(" ");

        var status = await check.EvaluateAsync(CancellationToken.None);

        status.ShouldBe(ReadinessCheckStatus.Degraded);
    }

    private static ContentStorageReadinessCheck CreateCheck(string rootPath) =>
        new(Options.Create(new ContentStoreOptions { RootPath = rootPath }));
}
