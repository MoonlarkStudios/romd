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

    internal static int Run(string[] args) => Run(args, TimeProvider.System, Console.Out, Console.Error);

    internal static int Run(string[] args, TimeProvider time, TextWriter output, TextWriter errors)
    {
        if (!TryParse(args, out string directory, out string[] hosts))
        {
            errors.WriteLine($"Usage: {Flag} <absolute-directory> [<dns-name-or-ip-address>...]");
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
                DeploymentCertificates.Outcome.Renewed => "Certificate: renewed; restart the ingress to serve it.",
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
