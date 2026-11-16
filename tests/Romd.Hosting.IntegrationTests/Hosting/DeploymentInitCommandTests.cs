using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Romd.Host.Configuration;
using Romd.Worker.Host;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class DeploymentInitCommandTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Directory.CreateTempSubdirectory("romd-init-").FullName;
    private readonly AdjustableTime _time = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _errors = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    public static TheoryData<string[]> InvalidArguments => new()
    {
        new string[] { "--init-deployment" },
        new string[] { "--INIT-DEPLOYMENT", "/secrets" },
        new string[] { "--init-deployment", "relative/secrets" },
        new string[] { "--init-deployment", "/secrets", "not a host" },
        new string[] { "--init-deployment", "/secrets", "https://nas.local" },
        new string[] { "--init-deployment", "/secrets", "nas.local", "NAS.local" },
        new string[] { "--migrate-database", "--init-deployment", "/secrets" }
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void Run_InvalidArguments_RefusesWithoutWriting(string[] args)
    {
        DeploymentInitCommand.IsRequested(args).ShouldBeTrue();

        RunRaw(args).ShouldBe(DeploymentInitCommand.InvalidArguments);

        Directory.EnumerateFileSystemEntries(_root).ShouldBeEmpty();
        _errors.ToString().ShouldContain("Usage:");
    }

    [Fact]
    public void Run_NewDirectory_GivesEachServiceOnlyItsOwnDatabasePasswords()
    {
        Run().ShouldBe(DeploymentInitCommand.Success);

        string[] roles = ["bootstrap", "provisioner", "worker", "admin", "consumer"];
        Dictionary<string, string> passwords = roles.ToDictionary(role => role, role => Read("postgres", role));
        passwords.Values.ShouldAllBe(password => password.Length == 64 && password.All(char.IsAsciiHexDigitLower));
        passwords.Values.Distinct().Count().ShouldBe(roles.Length);
        Read("worker", "pgpass").ShouldBe(
            $"*:*:*:romd_worker:{passwords["worker"]}\n*:*:*:romd_provisioner:{passwords["provisioner"]}\n");
        Read("admin", "pgpass").ShouldBe($"*:*:*:romd_admin:{passwords["admin"]}\n");
        Read("consumer", "pgpass").ShouldBe($"*:*:*:romd_consumer:{passwords["consumer"]}\n");
        _output.ToString().ShouldNotContain(passwords["admin"]);
    }

    [Fact]
    public void Run_NewDirectory_SharesSigningSecretsThroughHostConfiguration()
    {
        Run().ShouldBe(DeploymentInitCommand.Success);

        string[] services = ["worker", "admin", "consumer"];
        IConfiguration[] configurations = services
            .Select(service => new ConfigurationBuilder().AddRomdSecretFiles(Path.Combine(_root, service, "config")).Build())
            .ToArray<IConfiguration>();
        foreach (string key in new[] { "Romd:JwtSecret", "Romd:ConsumerDelivery:SigningSecret" })
        {
            string?[] values = configurations.Select(configuration => configuration[key]).ToArray();
            values[0].ShouldNotBeNull().Length.ShouldBe(64);
            values.Distinct().Count().ShouldBe(1);
        }

        configurations[0]["Romd:JwtSecret"].ShouldNotBe(configurations[0]["Romd:ConsumerDelivery:SigningSecret"]);
    }

    [Fact]
    public void Run_SecondRun_KeepsSecretsAndRepairsDerivedFiles()
    {
        Run().ShouldBe(DeploymentInitCommand.Success);
        string adminPassword = Read("postgres", "admin");
        string jwt = Read("worker", "config", DeploymentSecrets.JwtSecretKey);
        File.Delete(Path.Combine(_root, "admin", "pgpass"));
        File.Delete(Path.Combine(_root, "consumer", "config", DeploymentSecrets.JwtSecretKey));

        Run().ShouldBe(DeploymentInitCommand.Success);

        Read("postgres", "admin").ShouldBe(adminPassword);
        Read("admin", "pgpass").ShouldBe($"*:*:*:romd_admin:{adminPassword}\n");
        Read("consumer", "config", DeploymentSecrets.JwtSecretKey).ShouldBe(jwt);
        _output.ToString().ShouldContain("Secrets: all present.");
    }

    [Fact]
    public void Run_EmptySecretFile_FailsInsteadOfReplacingIt()
    {
        Run().ShouldBe(DeploymentInitCommand.Success);
        File.WriteAllText(Path.Combine(_root, "postgres", "worker"), "");

        Run().ShouldBe(DeploymentInitCommand.Failed);

        Read("postgres", "worker").ShouldBeEmpty();
        _errors.ToString().ShouldContain("postgres/worker");
    }

    [Fact]
    public void Run_WithoutHosts_IssuesNoCertificate()
    {
        Run().ShouldBe(DeploymentInitCommand.Success);

        Directory.Exists(Path.Combine(_root, "tls")).ShouldBeFalse();
    }

    [Fact]
    public void Run_WithHosts_IssuesServerCertificateTrustedOnlyThroughItsAuthority()
    {
        Run("nas.local", "192.168.1.50").ShouldBe(DeploymentInitCommand.Success);

        using X509Certificate2 authority = X509Certificate2.CreateFromPem(Read("tls", "ca.crt"));
        using X509Certificate2 server = X509Certificate2.CreateFromPemFile(
            Path.Combine(_root, "tls", "fullchain.pem"), Path.Combine(_root, "tls", "privkey.pem"));
        X509SubjectAlternativeNameExtension names = server.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        names.EnumerateDnsNames().ShouldBe(["nas.local"]);
        names.EnumerateIPAddresses().ShouldBe([IPAddress.Parse("192.168.1.50")]);
        (server.NotAfter.ToUniversalTime() - Start.UtcDateTime).ShouldBe(DeploymentCertificates.ServerLifetime);
        server.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single()
            .EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Single().Value.ShouldBe("1.3.6.1.5.5.7.3.1");
        X509Extension constraints = authority.Extensions.Single(extension => extension.Oid!.Value == "2.5.29.30");
        constraints.Critical.ShouldBeTrue();
        Chains(server, authority).ShouldBeTrue();
        File.Exists(Path.Combine(_root, "tls", "ca.key")).ShouldBeFalse();
        File.Exists(Path.Combine(_root, "ca", "ca.key")).ShouldBeTrue();
    }

    [Fact]
    public void Run_AuthorityAskedToSignAnotherName_IsRejectedByItsNameConstraints()
    {
        Run("nas.local").ShouldBe(DeploymentInitCommand.Success);
        using X509Certificate2 authority = X509Certificate2.CreateFromPemFile(
            Path.Combine(_root, "ca", "ca.crt"), Path.Combine(_root, "ca", "ca.key"));

        using X509Certificate2 permitted = Sign(authority, "nas.local");
        using X509Certificate2 forged = Sign(authority, "bank.example");

        Chains(permitted, authority).ShouldBeTrue();
        Chains(forged, authority).ShouldBeFalse();
    }

    [Fact]
    public void Run_CertificateFarFromExpiry_IsLeftUnchanged()
    {
        Run("nas.local").ShouldBe(DeploymentInitCommand.Success);
        string server = Read("tls", "fullchain.pem");
        _time.Advance(DeploymentCertificates.ServerLifetime - DeploymentCertificates.RenewalWindow - TimeSpan.FromDays(1));

        Run("nas.local").ShouldBe(DeploymentInitCommand.Success);

        Read("tls", "fullchain.pem").ShouldBe(server);
    }

    [Fact]
    public void Run_CertificateNearExpiry_RenewsUnderTheSameAuthority()
    {
        Run("nas.local").ShouldBe(DeploymentInitCommand.Success);
        string authority = Read("tls", "ca.crt");
        string server = Read("tls", "fullchain.pem");
        _time.Advance(DeploymentCertificates.ServerLifetime - DeploymentCertificates.RenewalWindow + TimeSpan.FromDays(1));

        Run("nas.local").ShouldBe(DeploymentInitCommand.Success);

        Read("tls", "ca.crt").ShouldBe(authority);
        Read("tls", "fullchain.pem").ShouldNotBe(server);
        _output.ToString().ShouldContain("renewed");
    }

    [Fact]
    public void Run_HostNamesChanged_IssuesNewAuthorityForTheNewNames()
    {
        Run("nas.local").ShouldBe(DeploymentInitCommand.Success);
        string authority = Read("tls", "ca.crt");

        Run("romd.example.com").ShouldBe(DeploymentInitCommand.Success);

        Read("tls", "ca.crt").ShouldNotBe(authority);
        using X509Certificate2 server = X509Certificate2.CreateFromPem(Read("tls", "fullchain.pem"));
        server.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames().ShouldBe(["romd.example.com"]);
    }

    [Fact]
    public void Run_OperatorSuppliedCertificate_IsNeverReplaced()
    {
        Directory.CreateDirectory(Path.Combine(_root, "tls"));
        File.WriteAllText(Path.Combine(_root, "tls", "fullchain.pem"), "operator certificate");
        File.WriteAllText(Path.Combine(_root, "tls", "privkey.pem"), "operator key");

        Run("nas.local").ShouldBe(DeploymentInitCommand.Success);

        Read("tls", "fullchain.pem").ShouldBe("operator certificate");
        Read("tls", "privkey.pem").ShouldBe("operator key");
        Directory.Exists(Path.Combine(_root, "ca")).ShouldBeFalse();
    }

    [Fact]
    public void AddRomdSecretFiles_ConfiguredDirectoryMissing_FailsInsteadOfStartingWithoutSecrets()
    {
        new ConfigurationBuilder().AddRomdSecretFiles(null).Build().AsEnumerable().ShouldBeEmpty();

        Should.Throw<DirectoryNotFoundException>(() =>
            new ConfigurationBuilder().AddRomdSecretFiles(Path.Combine(_root, "missing")).Build());
    }

    private static X509Certificate2 Sign(X509Certificate2 authority, string name)
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        CertificateRequest request = new($"CN={name}", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder alternatives = new();
        alternatives.AddDnsName(name);
        request.CertificateExtensions.Add(alternatives.Build());
        return request.Create(authority, Start.AddDays(-1), Start.AddDays(30), [1, 2, 3, 4]);
    }

    private int Run(params string[] hosts) =>
        DeploymentInitCommand.Run(["--init-deployment", _root, .. hosts], _time, _output, _errors);

    private int RunRaw(string[] args) => DeploymentInitCommand.Run(args, _time, _output, _errors);

    private string Read(params string[] path) => File.ReadAllText(Path.Combine([_root, .. path]));

    private bool Chains(X509Certificate2 certificate, X509Certificate2 authority)
    {
        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = Start.UtcDateTime;
        return chain.Build(certificate);
    }

    private sealed class AdjustableTime : TimeProvider
    {
        private DateTimeOffset _now = Start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
