using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Romd.Admin.Application.Source.Dat;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Dats;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Subscriptions;

public sealed class SignedDatCatalogClientTests : IDisposable
{
    private const string Site = "https://example.invalid";
    private readonly string workspace = Directory.CreateTempSubdirectory("romd-signed-reader-test-").FullName;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Read_ExclusiveReaderLock_ReturnsTransientBusy(bool discover)
    {
        var (client, lockPath) = CreateClient();
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var error = discover
            ? (await client.DiscoverAsync(default)).FirstError
            : (await client.FetchAsync("redump/psx/discs", "psx", "Sony - PlayStation", default)).FirstError;

        error.ShouldBe(DatEnrollmentErrors.Busy());
    }

    [Fact]
    public async Task Read_ReaderLockPathIsDirectory_ReturnsVerificationFailure()
    {
        var (client, lockPath) = CreateClient();
        Directory.CreateDirectory(lockPath);

        var result = await client.DiscoverAsync(default);

        result.FirstError.Code.ShouldBe("DatSubscription.CheckFailed");
    }

    private (SignedDatCatalogClient Client, string LockPath) CreateClient()
    {
        string root = Path.Combine(workspace, "root.json");
        File.WriteAllText(root, "{}");
        string rootHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(root)));
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rootHash + "\n" + Site + "\n")));
        string cache = Path.Combine(workspace, "dat-subscriptions", identity);
        Directory.CreateDirectory(cache);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatSubscriptions:Enabled"] = "true",
            ["DatSubscriptions:ReaderPath"] = Path.Combine(workspace, "reader"),
            ["DatSubscriptions:RootPath"] = root,
            ["DatSubscriptions:Site"] = Site
        }).Build();
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(workspace);
        return (new SignedDatCatalogClient(configuration, options), Path.Combine(cache, "reader.lock"));
    }

    public void Dispose() => Directory.Delete(workspace, recursive: true);
}
