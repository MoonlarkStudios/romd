using System.Net.Sockets;
using Shouldly;
using Xunit;

namespace Romd.Mount.Tests;

public sealed class MacBrokerCleanupTests
{
    [MacStateAclTests.MacAclFact]
    public void Cleanup_RejectsChangedIdentityAndUnexpectedEntries_ThenRemovesOwnedDirectory()
    {
        string directory = PrivateStateDirectory.Ensure("/private/tmp/romd-token-" + Guid.NewGuid().ToString("N"));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(Path.Combine(directory, "ipc")));
            var identity = MacDirectoryCleanup.CaptureSocketDirectory(directory);
            Should.Throw<IOException>(() => MacDirectoryCleanup.ClearSocketDirectory(identity with { DirectoryInode = identity.DirectoryInode + 1 }));
            Should.Throw<IOException>(() => MacDirectoryCleanup.ClearSocketDirectory(identity with { SocketInode = identity.SocketInode + 1 }));
            string unexpected = Path.Combine(directory, "preserve");
            File.WriteAllText(unexpected, "unrecognized data");
            Should.Throw<IOException>(() => MacDirectoryCleanup.ClearSocketDirectory(identity));
            File.ReadAllText(unexpected).ShouldBe("unrecognized data");
            File.Delete(unexpected);
            socket.Dispose();
            MacDirectoryCleanup.ClearSocketDirectory(identity);
            Directory.Exists(directory).ShouldBeFalse();
            MacDirectoryCleanup.ClearSocketDirectory(identity); // Retry after completed removal.
        }
        finally { socket.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [MacStateAclTests.MacAclFact]
    public void Cleanup_DirectoryReplacementWithLink_PreservesTarget()
    {
        string directory = "/private/tmp/romd-token-" + Guid.NewGuid().ToString("N");
        string target = PrivateStateDirectory.Ensure(directory + "-target");
        try
        {
            File.WriteAllText(Path.Combine(target, "ipc"), "preserve");
            Directory.CreateSymbolicLink(directory, target);
            Should.Throw<IOException>(() => MacDirectoryCleanup.ClearSocketDirectory(new(directory, 1, 2, 3)));
            File.ReadAllText(Path.Combine(target, "ipc")).ShouldBe("preserve");
            Should.Throw<IOException>(() => MacDirectoryCleanup.ClearSocketDirectory(new(target, 1, 2, 3)));
        }
        finally { Directory.Delete(directory); Directory.Delete(target, true); }
    }
}
