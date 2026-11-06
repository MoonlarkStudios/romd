using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Generation;
using Moonlark.Native.Engineering.Tests.TestSupport;
using Moonlark.Native.Engineering.Upstream;

namespace Moonlark.Native.Engineering.Tests.Upstream;

/// <summary>
/// A temp repository root whose submodule is a synthetic upstream: a tagged pinned commit and a later commit that
/// adds an export. The authorities are the real files with only the pinned identity substituted textually.
/// </summary>
internal sealed class UpstreamFixture : IDisposable
{
    internal const string FirstHeader =
        "CHD_EXPORT chd_error chd_open(const char *filename, int mode, chd_file *parent, chd_file **chd);\nCHD_EXPORT void chd_close(chd_file *chd);\n";
    internal const string SecondHeader = FirstHeader + "CHD_EXPORT chd_error chd_precache(chd_file *chd);\n";
    internal const string FirstExports = "chd_open\nchd_close\nmoonlark_chdr_build_info\n";
    internal const string SecondExports = "chd_open\nchd_close\nchd_precache\nmoonlark_chdr_build_info\n";

    private readonly TemporaryDirectory _directory = new();

    internal UpstreamFixture()
    {
        Root = _directory.Path;
        Source = Path.Combine(Root, GenerationConfiguration.SourcePath);
        Directory.CreateDirectory(Source);
        _ = ProcessRunner.Run(["git", "init", "-q", Source], TestRepository.CleanEnvironment).Value;
        FirstCommit = CommitHeaders(FirstHeader, "first coretypes\n");
        Git("-c", "tag.gpgSign=false", "tag", "v0.3.0");
        FirstVersion = Git("describe", "--always", "--tags", "--long");
        FirstDigests = Digests();
        SecondCommit = CommitHeaders(SecondHeader, "second coretypes\n");
        SecondVersion = Git("describe", "--always", "--tags", "--long");
        SecondDigests = Digests();
        Git("checkout", "--quiet", "--detach", FirstCommit);
        LibchdrPin real = Authorities.ReadLibchdr(TestRepository.Root).Value.Pin;
        Write(Authorities.PinPath, Substitute(Read(TestRepository.Root, Authorities.PinPath), real.Commit, FirstCommit, real.UpstreamVersion, FirstVersion,
            (real.Headers[Authorities.HeaderPaths[0]], FirstDigests[0]), (real.Headers[Authorities.HeaderPaths[1]], FirstDigests[1])));
        Write(Authorities.PropsPath, Substitute(Read(TestRepository.Root, Authorities.PropsPath), real.Commit, FirstCommit, real.UpstreamVersion, FirstVersion));
        Write(ExportInventory.AllowlistPath, FirstExports);
        Write(BindingGenerator.BindingsPath, "// previous bindings\n");
        Write(BindingGenerator.ContractPath, "// previous contract\n");
        Before = Tracked();
    }

    internal string Root { get; }

    internal string Source { get; }

    internal string FirstCommit { get; }

    internal string FirstVersion { get; }

    internal string[] FirstDigests { get; }

    internal string SecondCommit { get; }

    internal string SecondVersion { get; }

    internal string[] SecondDigests { get; }

    /// <summary>The tracked derived files as they were before any update ran.</summary>
    internal IReadOnlyDictionary<string, string> Before { get; }

    internal string Head => Git("rev-parse", "HEAD");

    internal IReadOnlyDictionary<string, string> Tracked() =>
        UpstreamUpdate.TrackedPaths.ToDictionary(path => path, path => Read(Root, path), StringComparer.Ordinal);

    internal string Read(string path) => Read(Root, path);

    internal string Git(params string[] arguments) => TestRepository.Git(Source, arguments);

    /// <summary>Replaces each old value with its new one; used to state exactly which bytes an update may change.</summary>
    internal static string Substitute(string text, string oldCommit, string newCommit, string oldVersion, string newVersion,
        params (string Old, string New)[] digests) =>
        digests.Aggregate(text.Replace(oldCommit, newCommit, StringComparison.Ordinal).Replace(oldVersion, newVersion, StringComparison.Ordinal),
            (current, digest) => current.Replace(digest.Old, digest.New, StringComparison.Ordinal));

    public void Dispose() => _directory.Dispose();

    private static string Read(string root, string path) => File.ReadAllText(Path.Combine(root, path));

    private void Write(string path, string text)
    {
        string full = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private string CommitHeaders(string chd, string coreTypes)
    {
        File.WriteAllText(HeaderPath(0), chd);
        File.WriteAllText(HeaderPath(1), coreTypes);
        Git("add", ".");
        Git("-c", "user.name=Synthetic Test", "-c", "user.email=synthetic@example.invalid", "-c", "commit.gpgsign=false",
            "commit", "-qm", "synthetic upstream");
        return Git("rev-parse", "HEAD");
    }

    private string[] Digests() => [Digest.Sha256File(HeaderPath(0)), Digest.Sha256File(HeaderPath(1))];

    private string HeaderPath(int index)
    {
        string path = Path.Combine(Source, Authorities.HeaderPaths[index]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
