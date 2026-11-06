using System.Globalization;

namespace Moonlark.Native.Engineering.Core;

/// <summary>Proves an upstream checkout is exactly the pinned, unmodified commit, independently of index shortcuts.</summary>
internal static class SourceIdentity
{
    /// <summary>Returns the pinned commit's timestamp, used as SOURCE_DATE_EPOCH.</summary>
    internal static Result<long> Verify(string source, LibchdrPin pin, IReadOnlyDictionary<string, string> environment, TextWriter? log = null)
    {
        Result<IReadOnlyDictionary<string, string>> created = BuildEnvironment.Create(environment);
        if (!created.Succeeded)
            return created.Failure;
        IReadOnlyDictionary<string, string> env = created.Value;
        if (Check.That(Directory.Exists(source) && Path.Exists(Path.Combine(source, ".git")),
            "Source must be an existing git checkout") is { } checkout)
            return checkout;
        Result<string> head = ProcessRunner.Run(["git", "-C", source, "rev-parse", "HEAD"], env, log);
        if (!head.Succeeded)
            return head.Failure;
        if (Check.That(head.Value == pin.Commit, "Upstream checkout does not match pinned commit") is { } commit)
            return commit;
        Result<string> status = ProcessRunner.Run(["git", "-C", source, "status", "--porcelain", "--ignored", "--untracked-files=all"], env, log);
        if (!status.Succeeded)
            return status.Failure;
        if (Check.That(status.Value.Length == 0, "Upstream checkout is dirty (including ignored files)") is { } dirty)
            return dirty;
        if (VerifyTrackedBytes(source, env, log) is { } tracked)
            return tracked;
        Result<string> describe = ProcessRunner.Run(["git", "-C", source, "describe", "--always", "--tags", "--long"], env, log);
        if (!describe.Succeeded)
            return describe.Failure;
        if (Check.That(describe.Value == pin.UpstreamVersion, "Upstream describe identity does not match pin") is { } identity)
            return identity;
        foreach ((string name, string digest) in pin.Headers)
            if (Check.That(Digest.Sha256File(Path.Combine(source, name)) == digest, "Upstream header digest mismatch: " + name) is { } header)
                return header;
        Result<string> epoch = ProcessRunner.Run(["git", "-C", source, "show", "-s", "--format=%ct", "HEAD"], env, log);
        if (!epoch.Succeeded)
            return epoch.Failure;
        return epoch.Value.Length > 0 && epoch.Value.All(char.IsAsciiDigit)
            ? long.Parse(epoch.Value, CultureInfo.InvariantCulture)
            : new Failure("Invalid pinned source timestamp");
    }

    /// <summary>Compares working-tree bytes with committed blobs, so assume-unchanged and skip-worktree cannot hide edits.</summary>
    internal static Failure? VerifyTrackedBytes(string source, IReadOnlyDictionary<string, string> environment, TextWriter? log = null)
    {
        ProcessOutput listing = ProcessRunner.Execute(["git", "--no-replace-objects", "-C", source, "ls-tree", "--full-tree", "-r", "-z", "HEAD"], environment, log);
        if (listing.ExitCode != 0)
            return new Failure("Could not list the pinned source tree: " + listing.Stderr.Trim());
        foreach (string entry in listing.Stdout.TrimEnd('\0').Split('\0'))
        {
            string[] identity = entry.Split('\t', 2);
            string[] fields = identity[0].Split(' ');
            if (identity.Length != 2 || fields.Length != 3)
                return new Failure("Unrecognized git tree entry: " + entry);
            (string mode, string kind, string expected, string name) = (fields[0], fields[1], fields[2], identity[1]);
            string[] parts = name.Split('/');
            if (Path.IsPathRooted(name) || parts.Contains(".."))
                return new Failure("Unsafe path in committed source tree");
            if (kind != "blob" || (mode != "100644" && mode != "100755"))
                return new Failure("Unsupported tracked source mode: " + name);
            string path = Path.Combine([source, .. parts]);
            if (!File.Exists(path) || ArtifactsPath.IsLink(path))
                return new Failure("Missing or substituted tracked source: " + name);
            if (Digest.GitBlobSha1(File.ReadAllBytes(path)) != expected)
                return new Failure("Tracked source bytes disagree with pinned tree: " + name);
        }
        return null;
    }
}
