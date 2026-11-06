using System.Collections.Immutable;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Generation;

namespace Moonlark.Native.Engineering.Upstream;

/// <summary>Regenerates the bindings and contract after the pin moves; tests inject a recording or failing step.</summary>
internal delegate Failure? Regenerate(string root);

internal sealed record UpstreamUpdateResult(string PreviousCommit, string Commit, string UpstreamVersion, bool ExportsChanged);

/// <summary>
/// Moves the libchdr pin to a commit already present locally and regenerates everything derived from it, all or
/// nothing: any failure after the checkout restores the tracked derived files and the previous commit.
/// </summary>
internal static class UpstreamUpdate
{
    /// <summary>Every tracked file the update may write, restored byte-for-byte on failure.</summary>
    internal static ImmutableArray<string> TrackedPaths { get; } =
        [Authorities.PinPath, Authorities.PropsPath, ExportInventory.AllowlistPath, BindingGenerator.BindingsPath, BindingGenerator.ContractPath];

    /// <summary>This command never fetches; the scheduled job or a maintainer runs this first.</summary>
    internal static string FetchCommand(string commit) =>
        $"git -C {GenerationConfiguration.SourcePath} fetch --tags {Authorities.Repository} {commit}";

    internal static Result<UpstreamUpdateResult> Run(string root, string commit, IReadOnlyDictionary<string, string> environment, Regenerate regenerate)
    {
        if (Check.That(Authorities.Sha1().IsMatch(commit), "Upstream commit must be a full 40-character lowercase SHA-1") is { } format)
            return format;
        Result<LibchdrAuthority> current = Authorities.ReadLibchdr(root);
        if (!current.Succeeded)
            return current.Failure;
        Result<IReadOnlyDictionary<string, string>> env = BuildEnvironment.Create(environment);
        if (!env.Succeeded)
            return env.Failure;
        string source = Path.Combine(root, GenerationConfiguration.SourcePath);
        if (Check.That(Directory.Exists(source) && Path.Exists(Path.Combine(source, ".git")),
            "Upstream source must be an existing git checkout: " + GenerationConfiguration.SourcePath) is { } checkout)
            return checkout;
        if (ProcessRunner.Execute(["git", "-C", source, "cat-file", "-e", commit + "^{commit}"], env.Value).ExitCode != 0)
            return new Failure($"Upstream commit {commit} is not present locally and is never fetched here. Fetch it first:\n  {FetchCommand(commit)}");
        Result<long> clean = SourceIdentity.Verify(source, current.Value.Pin, env.Value);
        if (!clean.Succeeded)
            return new Failure("Upstream checkout must be the clean current pin before an update: " + clean.Failure.Message);
        IReadOnlyDictionary<string, byte[]?> snapshot = Snapshot(root);
        try
        {
            Result<UpstreamUpdateResult> applied = Apply(root, source, commit, current.Value.Pin, env.Value, regenerate);
            if (applied.Succeeded)
                return applied;
            return Rollback(root, source, current.Value.Pin, snapshot, env.Value) is { } rollback
                ? new Failure($"Upstream update failed: {applied.Failure.Message}\nROLLBACK FAILED, restore manually: {rollback.Message}")
                : new Failure($"Upstream update failed and was rolled back to {current.Value.Pin.Commit}: {applied.Failure.Message}");
        }
        catch
        {
            // Unexpected I/O still restores the previous state before reaching the command boundary.
            _ = Rollback(root, source, current.Value.Pin, snapshot, env.Value);
            throw;
        }
    }

    internal static string ExportList(IEnumerable<string> exports) => string.Join('\n', exports) + "\n";

    private sealed record DerivedAuthorities(string Pin, string Props, string Exports);

    private static Result<UpstreamUpdateResult> Apply(string root, string source, string commit, LibchdrPin previous,
        IReadOnlyDictionary<string, string> environment, Regenerate regenerate)
    {
        Result<string> checkout = ProcessRunner.Run(["git", "-C", source, "checkout", "--quiet", "--detach", commit], environment);
        if (!checkout.Succeeded)
            return checkout.Failure;
        Result<string> describe = ProcessRunner.Run(["git", "-C", source, "describe", "--always", "--tags", "--long"], environment);
        if (!describe.Succeeded)
            return describe.Failure;
        Result<DerivedAuthorities> derived = Derive(root, source, commit, describe.Value);
        if (!derived.Succeeded)
            return derived.Failure;
        string exports = Path.Combine(root, ExportInventory.AllowlistPath);
        bool exportsChanged = !File.Exists(exports) || File.ReadAllText(exports) != derived.Value.Exports;
        File.WriteAllText(Path.Combine(root, Authorities.PinPath), derived.Value.Pin);
        File.WriteAllText(Path.Combine(root, Authorities.PropsPath), derived.Value.Props);
        File.WriteAllText(exports, derived.Value.Exports);
        Result<LibchdrAuthority> updated = Authorities.ReadLibchdr(root);
        if (!updated.Succeeded)
            return updated.Failure;
        Result<long> identity = SourceIdentity.Verify(source, updated.Value.Pin, environment);
        if (!identity.Succeeded)
            return identity.Failure;
        if (regenerate(root) is { } generation)
            return new Failure("Regeneration failed: " + generation.Message);
        return new UpstreamUpdateResult(previous.Commit, commit, describe.Value, exportsChanged);
    }

    /// <summary>Computes every rewritten authority before the first write.</summary>
    private static Result<DerivedAuthorities> Derive(string root, string source, string commit, string upstreamVersion)
    {
        if (Authorities.HeaderPaths.FirstOrDefault(name => !File.Exists(Path.Combine(source, name))) is { } absent)
            return new Failure("Upstream commit lacks ABI header: " + absent);
        Dictionary<string, string> headers = Authorities.HeaderPaths.ToDictionary(name => name,
            name => Digest.Sha256File(Path.Combine(source, name)), StringComparer.Ordinal);
        Result<string> pin = AuthorityText.RewritePin(File.ReadAllText(Path.Combine(root, Authorities.PinPath)), commit, upstreamVersion, headers);
        if (!pin.Succeeded)
            return pin.Failure;
        Result<string> props = AuthorityText.RewriteProps(File.ReadAllText(Path.Combine(root, Authorities.PropsPath)), commit, upstreamVersion);
        if (!props.Succeeded)
            return props.Failure;
        Result<ImmutableArray<string>> exports = ExportInventory.FromHeader(File.ReadAllText(Path.Combine(source, Authorities.HeaderPaths[0])));
        if (!exports.Succeeded)
            return exports.Failure;
        return new DerivedAuthorities(pin.Value, props.Value, ExportList(exports.Value));
    }

    private static Dictionary<string, byte[]?> Snapshot(string root) =>
        TrackedPaths.ToDictionary(path => path, path => File.Exists(Path.Combine(root, path)) ? File.ReadAllBytes(Path.Combine(root, path)) : null,
            StringComparer.Ordinal);

    /// <summary>Attempts every restoration step, then proves the previous pin is checked out cleanly again.</summary>
    private static Failure? Rollback(string root, string source, LibchdrPin previous, IReadOnlyDictionary<string, byte[]?> snapshot,
        IReadOnlyDictionary<string, string> environment)
    {
        List<string> problems = [];
        foreach ((string path, byte[]? bytes) in snapshot)
        {
            try
            {
                if (bytes is null)
                    File.Delete(Path.Combine(root, path));
                else
                    File.WriteAllBytes(Path.Combine(root, path), bytes);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                problems.Add(path + ": " + exception.Message);
            }
        }
        Result<string> checkout = ProcessRunner.Run(["git", "-C", source, "checkout", "--quiet", "--detach", previous.Commit], environment);
        if (!checkout.Succeeded)
            problems.Add(checkout.Failure.Message);
        else if (SourceIdentity.Verify(source, previous, environment) is { Succeeded: false } restored)
            problems.Add(restored.Failure.Message);
        return problems.Count == 0 ? null : new Failure(string.Join("; ", problems));
    }
}
