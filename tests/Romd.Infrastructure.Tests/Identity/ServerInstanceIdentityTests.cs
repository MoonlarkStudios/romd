using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Identity;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Identity;

public sealed class ServerInstanceIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"romd-server-identity-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task StartAsync_FreshDataDirectory_CreatesCanonicalExactIdentity()
    {
        string dataDirectory = CreateDataDirectory();
        var identity = await InitializeAsync(dataDirectory);

        string path = ServerInstanceIdentity.ResolvePath(dataDirectory);
        string content = await File.ReadAllTextAsync(path);

        content.ShouldBe(identity.InstanceId.ToString("D"));
        content.Length.ShouldBe(36);
        content.ShouldBe(content.ToLowerInvariant());
    }

    [Fact]
    public async Task StartAsync_ConcurrentCreators_ConvergeOnOnePublishedIdentity()
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string dataDirectory = CreateDataDirectory();

            Guid[] instanceIds = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ =>
                Task.Run(async () => (await InitializeAsync(dataDirectory)).InstanceId)));

            instanceIds.Distinct().ShouldHaveSingleItem();
            (await File.ReadAllTextAsync(ServerInstanceIdentity.ResolvePath(dataDirectory)))
                .ShouldBe(instanceIds[0].ToString("D"));
            Directory.GetFiles(
                    Path.GetDirectoryName(ServerInstanceIdentity.ResolvePath(dataDirectory))!,
                    "server-instance-id.*.tmp")
                .ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task StartAsync_RecognizableCrashOrphanWithoutAuthority_PublishesThenReapsOrphan()
    {
        string dataDirectory = CreateDataDirectory();
        string orphanPath = CreateRecognizableTempPath(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(orphanPath)!);
        await File.WriteAllTextAsync(orphanPath, Guid.NewGuid().ToString("D"));

        var identity = await InitializeAsync(dataDirectory);

        File.Exists(orphanPath).ShouldBeFalse();
        (await File.ReadAllTextAsync(ServerInstanceIdentity.ResolvePath(dataDirectory)))
            .ShouldBe(identity.InstanceId.ToString("D"));
    }

    [Fact]
    public async Task StartAsync_RecognizableCrashOrphanWithValidAuthority_ReapsOrphan()
    {
        string dataDirectory = CreateDataDirectory();
        Guid expected = (await InitializeAsync(dataDirectory)).InstanceId;
        string orphanPath = CreateRecognizableTempPath(dataDirectory);
        await File.WriteAllTextAsync(orphanPath, Guid.NewGuid().ToString("D"));

        Guid restarted = (await InitializeAsync(dataDirectory)).InstanceId;

        restarted.ShouldBe(expected);
        File.Exists(orphanPath).ShouldBeFalse();
    }

    [Fact]
    public async Task StartAsync_CleanupPreservesUnrelatedAndUnrecognizedFiles()
    {
        string dataDirectory = CreateDataDirectory();
        await InitializeAsync(dataDirectory);
        string identityDirectory = Path.GetDirectoryName(ServerInstanceIdentity.ResolvePath(dataDirectory))!;
        string unrelatedPath = Path.Combine(identityDirectory, "operator-note.tmp");
        string malformedTempPath = Path.Combine(identityDirectory, "server-instance-id.not-a-guid.tmp");
        string uppercaseTempPath = Path.Combine(
            identityDirectory,
            $"server-instance-id.{Guid.NewGuid():N}.tmp".ToUpperInvariant());
        await File.WriteAllTextAsync(unrelatedPath, "keep");
        await File.WriteAllTextAsync(malformedTempPath, "keep");
        await File.WriteAllTextAsync(uppercaseTempPath, "keep");

        await InitializeAsync(dataDirectory);

        (await File.ReadAllTextAsync(unrelatedPath)).ShouldBe("keep");
        (await File.ReadAllTextAsync(malformedTempPath)).ShouldBe("keep");
        (await File.ReadAllTextAsync(uppercaseTempPath)).ShouldBe("keep");
    }

    [Fact]
    public async Task StartAsync_OpenRecognizableTemp_IsSafelyRetriedAfterOwnerCloses()
    {
        string dataDirectory = CreateDataDirectory();
        Guid expected = (await InitializeAsync(dataDirectory)).InstanceId;
        string orphanPath = CreateRecognizableTempPath(dataDirectory);
        await File.WriteAllTextAsync(orphanPath, Guid.NewGuid().ToString("D"));

        await using (var owner = new FileStream(
                         orphanPath,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.None))
        {
            Guid observed = (await InitializeAsync(dataDirectory)).InstanceId;
            observed.ShouldBe(expected);

            if (OperatingSystem.IsWindows())
            {
                File.Exists(orphanPath).ShouldBeTrue();
            }
        }

        Guid restarted = (await InitializeAsync(dataDirectory)).InstanceId;

        restarted.ShouldBe(expected);
        File.Exists(orphanPath).ShouldBeFalse();
    }

    [Fact]
    public async Task StartAsync_Restart_PreservesIdentity()
    {
        string dataDirectory = CreateDataDirectory();

        Guid first = (await InitializeAsync(dataDirectory)).InstanceId;
        Guid restarted = (await InitializeAsync(dataDirectory)).InstanceId;

        restarted.ShouldBe(first);
    }

    [Fact]
    public async Task StartAsync_SeparateDataDirectories_CreateDifferentIdentities()
    {
        Guid first = (await InitializeAsync(CreateDataDirectory())).InstanceId;
        Guid second = (await InitializeAsync(CreateDataDirectory())).InstanceId;

        second.ShouldNotBe(first);
    }

    [Fact]
    public async Task StartAsync_CopiedLogicalRestore_PreservesIdentity()
    {
        string source = CreateDataDirectory();
        Guid sourceId = (await InitializeAsync(source)).InstanceId;
        string restored = CreateDataDirectory();
        string restoredIdentityPath = ServerInstanceIdentity.ResolvePath(restored);
        Directory.CreateDirectory(Path.GetDirectoryName(restoredIdentityPath)!);
        File.Copy(ServerInstanceIdentity.ResolvePath(source), restoredIdentityPath);

        Guid restoredId = (await InitializeAsync(restored)).InstanceId;

        restoredId.ShouldBe(sourceId);
    }

    [Fact]
    public async Task StartAsync_IndependentCloneRotatedBeforeStartup_CreatesDifferentIdentity()
    {
        string source = CreateDataDirectory();
        Guid sourceId = (await InitializeAsync(source)).InstanceId;
        string clone = CreateDataDirectory();
        string cloneIdentityPath = ServerInstanceIdentity.ResolvePath(clone);
        Directory.CreateDirectory(Path.GetDirectoryName(cloneIdentityPath)!);
        File.Copy(ServerInstanceIdentity.ResolvePath(source), cloneIdentityPath);

        // Clone rotation is an offline operator workflow: stop the copied hosts and delete only
        // the copied public identity before the independent clone's first startup.
        File.Delete(cloneIdentityPath);
        Guid cloneId = (await InitializeAsync(clone)).InstanceId;

        cloneId.ShouldNotBe(sourceId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("6F9619FF-8B86-D011-B42D-00C04FC964FF")]
    [InlineData("6f9619ff-8b86-d011-b42d-00c04fc964ff\n")]
    [InlineData("\uFEFF6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    public async Task StartAsync_InvalidExistingIdentity_FailsWithoutReplacing(string content)
    {
        string dataDirectory = CreateDataDirectory();
        string identityPath = ServerInstanceIdentity.ResolvePath(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(identityPath)!);
        await File.WriteAllTextAsync(identityPath, content);
        byte[] originalBytes = await File.ReadAllBytesAsync(identityPath);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await StartAsync(new ServerInstanceIdentity(CreateOptions(dataDirectory))));

        (await File.ReadAllBytesAsync(identityPath)).ShouldBe(originalBytes);
    }

    [Fact]
    public async Task StartAsync_CorruptAuthority_DoesNotReapRecognizableOrphan()
    {
        string dataDirectory = CreateDataDirectory();
        string identityPath = ServerInstanceIdentity.ResolvePath(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(identityPath)!);
        await File.WriteAllTextAsync(identityPath, "corrupt");
        string orphanPath = CreateRecognizableTempPath(dataDirectory);
        await File.WriteAllTextAsync(orphanPath, Guid.NewGuid().ToString("D"));

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await InitializeAsync(dataDirectory));

        File.Exists(orphanPath).ShouldBeTrue();
        (await File.ReadAllTextAsync(identityPath)).ShouldBe("corrupt");
    }

    [Fact]
    public async Task StartAsync_UnreadableExistingIdentity_FailsWithoutReplacing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string dataDirectory = CreateDataDirectory();
        string identityPath = ServerInstanceIdentity.ResolvePath(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(identityPath)!);
        const string content = "6f9619ff-8b86-d011-b42d-00c04fc964ff";
        await File.WriteAllTextAsync(identityPath, content);
        UnixFileMode originalMode = File.GetUnixFileMode(identityPath);

        try
        {
            File.SetUnixFileMode(identityPath, UnixFileMode.None);

            await Should.ThrowAsync<UnauthorizedAccessException>(async () =>
                await StartAsync(new ServerInstanceIdentity(CreateOptions(dataDirectory))));
        }
        finally
        {
            File.SetUnixFileMode(identityPath, originalMode);
        }

        (await File.ReadAllTextAsync(identityPath)).ShouldBe(content);
    }

    [Fact]
    public void InstanceId_BeforeStartupInitialization_Throws()
    {
        var identity = new ServerInstanceIdentity(CreateOptions(CreateDataDirectory()));

        Should.Throw<InvalidOperationException>(() => _ = identity.InstanceId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateDataDirectory()
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateRecognizableTempPath(string dataDirectory) =>
        Path.Combine(
            Path.GetDirectoryName(ServerInstanceIdentity.ResolvePath(dataDirectory))!,
            $"server-instance-id.{Guid.NewGuid():N}.tmp");

    private static async Task<ServerInstanceIdentity> InitializeAsync(string dataDirectory)
    {
        var identity = new ServerInstanceIdentity(CreateOptions(dataDirectory));
        await StartAsync(identity);
        return identity;
    }

    private static Task StartAsync(ServerInstanceIdentity identity) =>
        new ServerInstanceIdentityInitializer(identity).StartAsync(CancellationToken.None);

    private static IRomdOptions CreateOptions(string dataDirectory)
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(dataDirectory);
        return options;
    }
}
