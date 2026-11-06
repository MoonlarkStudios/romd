namespace Romd.Media.Probe;

/// <summary>Real-disc probe fixtures, located only through <c>ROMD_FIXTURES</c>; real-disc probes stay opt-in.</summary>
/// <remarks>The root holds <c>manifest.json</c> and the <c>ps2/</c> images. Probes compare recorded host paths with it
/// and never search a default location; containerised probes may receive the host root without its contents.</remarks>
internal static class ProbeFixtures
{
    public const string Variable = "ROMD_FIXTURES";

    public static string FromEnvironment() => Resolve(Environment.GetEnvironmentVariable(Variable));

    public static string FinalFantasyX(string root) => Path.Combine(root, "ps2", "Final Fantasy X (USA).iso");
    public static string GranTurismo4(string root) => Path.Combine(root, "ps2", "Gran Turismo 4 (USA).iso");

    internal static string Resolve(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || root.Any(char.IsControl) || !Path.IsPathFullyQualified(root))
            throw new InvalidOperationException($"Real-disc probes require {Variable}: the absolute fixture root holding manifest.json, " +
                "ps2/Final Fantasy X (USA).iso and ps2/Gran Turismo 4 (USA).iso.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }
}
