namespace Romd.Worker.Host;

/// <summary>
///     Prepares a new installation without configuration or a database: generates its secrets and, when no
///     certificate is supplied, its HTTPS certificate. Safe to run on every start; existing secrets are kept.
/// </summary>
internal static class DeploymentInitCommand
{
    internal const string Flag = "--init-deployment";
    internal const int Success = 0;
    internal const int Failed = 1;
    internal const int InvalidArguments = 2;

    /// <summary>The public origins Compose derived for the three surfaces, space separated.</summary>
    internal const string PublicOriginsVariable = "ROMD_PUBLIC_ORIGINS";

    internal static bool IsRequested(string[] args) =>
        args.Any(argument => argument.StartsWith(Flag, StringComparison.OrdinalIgnoreCase));

    internal static bool TryParse(string[] args, out string directory, out string[] hosts)
    {
        directory = "";
        hosts = [];
        if (args.Length < 2 || args[0] != Flag || !Path.IsPathFullyQualified(args[1]))
            return false;
        hosts = args[2..];
        if (!hosts.All(DeploymentCertificates.IsValidHost)
            || hosts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != hosts.Length)
            return false;
        directory = args[1];
        return true;
    }

    private static bool IsHttpsOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Length > 0
        && uri.AbsolutePath == "/"
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0;

    internal static int Run(string[] args) => Run(
        args, TimeProvider.System, Console.Out, Console.Error, Environment.GetEnvironmentVariable(PublicOriginsVariable));

    internal static int Run(
        string[] args, TimeProvider time, TextWriter output, TextWriter errors, string? publicOrigins = null)
    {
        if (!TryParse(args, out string directory, out string[] hosts))
        {
            errors.WriteLine($"Usage: {Flag} <absolute-directory> [<dns-name-or-ip-address>...]");
            return InvalidArguments;
        }

        // Browsers send origins in lower case and the applications compare them exactly, so a
        // mixed-case name would start a stack nobody can sign in to. Refuse it before anything is written.
        if (hosts.FirstOrDefault(host => host != host.ToLowerInvariant()) is { } mixedCase)
        {
            errors.WriteLine($"Host names must be lower case: use {mixedCase.ToLowerInvariant()} in place of {mixedCase} (ROMD_HOST).");
            return InvalidArguments;
        }

        // The origins are derived from ROMD_HOST unless set explicitly. Without it the stack would start
        // and fail later with errors that never mention the setting.
        string[] origins = (publicOrigins ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (origins.FirstOrDefault(origin => !IsHttpsOrigin(origin)) is { } invalid)
        {
            errors.WriteLine(invalid.StartsWith("https://:", StringComparison.Ordinal)
                ? "ROMD_HOST is not set. Set it in .env to the name or IP address devices on your network use "
                  + "to reach this server, for example ROMD_HOST=nas.local."
                : $"Public origin '{invalid}' must be an https:// origin without a path, such as https://nas.local:7664.");
            return InvalidArguments;
        }

        try
        {
            IReadOnlyList<string> created = DeploymentSecrets.Ensure(directory);
            output.WriteLine(created.Count == 0
                ? "Secrets: all present."
                : $"Secrets: generated {string.Join(", ", created)}.");
            if (hosts.Length == 0)
            {
                output.WriteLine("Certificate: no host names given; skipped.");
                return Success;
            }

            output.WriteLine(DeploymentCertificates.Ensure(directory, hosts, time.GetUtcNow()) switch
            {
                DeploymentCertificates.Outcome.OperatorSupplied => "Certificate: using the supplied certificate.",
                DeploymentCertificates.Outcome.Current => "Certificate: current.",
                DeploymentCertificates.Outcome.Renewed => "Certificate: reissued under the existing authority; the ingress serves it within a minute.",
                _ => $"Certificate: issued a new authority and certificate for {string.Join(", ", hosts)}. "
                     + $"Trust {DeploymentCertificates.PublishedAuthority} on each device."
            });
            return Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                              or System.Security.Cryptography.CryptographicException)
        {
            errors.WriteLine($"Deployment initialization failed: {exception.Message}");
            return Failed;
        }
    }
}
