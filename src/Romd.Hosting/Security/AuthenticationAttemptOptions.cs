namespace Romd.Hosting.Security;

public sealed class AuthenticationAttemptOptions
{
    public const string SectionName = "Romd:Auth:Attempts";

    public int IpLimit { get; set; } = 30;
    public int AccountLimit { get; set; } = 10;
    public int WindowSeconds { get; set; } = 60;
    public int MaxIpKeys { get; set; } = 4096;
    public int MaxAccountKeys { get; set; } = 8192;
    public int MaxConcurrentVerifications { get; set; } = 2;

    public void Validate()
    {
        if (IpLimit is < 1 or > 10000 || AccountLimit is < 1 or > 10000
            || WindowSeconds is < 1 or > 3600 || MaxIpKeys is < 1 or > 65536
            || MaxAccountKeys is < 1 or > 65536 || MaxConcurrentVerifications is < 1 or > 32)
            throw new InvalidOperationException($"{SectionName} contains an out-of-range authentication attempt limit.");
    }
}
