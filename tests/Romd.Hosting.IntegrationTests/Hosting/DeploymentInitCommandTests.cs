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
    public void Run_MixedCaseHostName_IsRefusedWithTheLowerCaseNameToUse()
    {
        Run("NAS.example.test").ShouldBe(DeploymentInitCommand.InvalidArguments);

        _errors.ToString().ShouldContain("use nas.example.test in place of NAS.example.test");
        Directory.EnumerateFileSystemEntries(_root).ShouldBeEmpty();
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
    public void Run_NewDirectory_StoresSigningSecretsInTheDataDirectoryForHostConfiguration()
    {
        Run().ShouldBe(DeploymentInitCommand.Success);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddRomdSecretFiles(Path.Combine(_root, "data", "secrets")).Build();
        string jwt = configuration["Romd:JwtSecret"].ShouldNotBeNull();
        string delivery = configuration["Romd:ConsumerDelivery:SigningSecret"].ShouldNotBeNull();
        jwt.Length.ShouldBe(64);
        delivery.Length.ShouldBe(64);
        jwt.ShouldNotBe(delivery);
        Directory.EnumerateFiles(_root, "Romd__*", SearchOption.AllDirectories).Count().ShouldBe(2);
    }

    [Fact]
    public void Run_SecondRun_KeepsSecretsAndRepairsDerivedFiles()
    {
        Run().ShouldBe(DeploymentInitCommand.Success);
        string adminPassword = Read("postgres", "admin");
        string jwt = Read("data", "secrets", DeploymentSecrets.JwtSecretKey);
        File.Delete(Path.Combine(_root, "admin", "pgpass"));

        Run().ShouldBe(DeploymentInitCommand.Success);

        Read("postgres", "admin").ShouldBe(adminPassword);
        Read("admin", "pgpass").ShouldBe($"*:*:*:romd_admin:{adminPassword}\n");
        Read("data", "secrets", DeploymentSecrets.JwtSecretKey).ShouldBe(jwt);
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

    [Theory]
    [InlineData("192.168.1.50", "bank.example")]
    [InlineData("192.168.1.50", "192.168.1.51")]
    [InlineData("192.168.1.50", "2001:db8::1")]
    [InlineData("nas.local", "192.168.1.50")]
    [InlineData("nas.local", "2001:db8::1")]
    [InlineData("nas.local", "local")]
    [InlineData("fd00::50", "bank.example")]
    [InlineData("fd00::50", "192.168.1.50")]
    public void Run_AuthorityAskedToSignANameOfAnyOtherKind_IsRejectedByItsNameConstraints(string host, string other)
    {
        Run(host).ShouldBe(DeploymentInitCommand.Success);
        using X509Certificate2 authority = X509Certificate2.CreateFromPemFile(
            Path.Combine(_root, "ca", "ca.crt"), Path.Combine(_root, "ca", "ca.key"));

        using X509Certificate2 issued = X509Certificate2.CreateFromPem(Read("tls", "fullchain.pem"));
        using X509Certificate2 permitted = Sign(authority, host);
        using X509Certificate2 forged = Sign(authority, other);

        Chains(issued, authority).ShouldBeTrue("the certificate ROMD issued for this installation");
        Chains(permitted, authority).ShouldBeTrue();
        Chains(forged, authority).ShouldBeFalse();
    }

    [Theory]
    [InlineData("192.168.1.50")]
    [InlineData("nas.local")]
    [InlineData("fd00::50")]
    [InlineData("192.168.1.50", "nas.local", "fd00::50")]
    public void Run_IssuedServerCertificate_ChainsAndNamesHostsOnlyAsAlternativeNames(params string[] hosts)
    {
        Run(hosts).ShouldBe(DeploymentInitCommand.Success);

        using X509Certificate2 authority = X509Certificate2.CreateFromPem(Read("tls", "ca.crt"));
        using X509Certificate2 server = X509Certificate2.CreateFromPem(Read("tls", "fullchain.pem"));
        Chains(server, authority).ShouldBeTrue();
        // A host-like common name is read as a DNS name by OpenSSL and checked against the DNS constraint.
        server.Subject.ShouldBe(DeploymentCertificates.ServerSubject);
        hosts.ShouldAllBe(host => !server.Subject.Contains(host, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Run_MixedNames_PermitExactlyThoseNamesAndTheirSubdomains()
    {
        Run("nas.local", "192.168.1.50", "fd00::50").ShouldBe(DeploymentInitCommand.Success);
        using X509Certificate2 authority = X509Certificate2.CreateFromPemFile(
            Path.Combine(_root, "ca", "ca.crt"), Path.Combine(_root, "ca", "ca.key"));

        foreach (string permitted in new[] { "nas.local", "media.nas.local", "192.168.1.50", "fd00::50" })
        {
            using X509Certificate2 certificate = Sign(authority, permitted);
            Chains(certificate, authority).ShouldBeTrue(permitted);
        }

        foreach (string refused in new[] { "bank.example", "local", "notnas.local", "192.168.1.51", "fd00::51", "10.0.0.1" })
        {
            using X509Certificate2 certificate = Sign(authority, refused);
            Chains(certificate, authority).ShouldBeFalse(refused);
        }
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
        _output.ToString().ShouldContain("reissued under the existing authority");
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
    public void Run_RestoredAuthority_IsKeptAndTheServerCertificateReissuedUnderIt()
    {
        // The installation devices already trust, as its backup captured it.
        Run("nas.local").ShouldBe(DeploymentInitCommand.Success);
        string trusted = Read("tls", "ca.crt");
        string backup = Directory.CreateTempSubdirectory("romd-init-backup-").FullName;
        CopyDirectory(Path.Combine(_root, "ca"), backup);
        // A fresh installation initializes first, then the restore replaces its authority.
        Directory.Delete(_root, recursive: true);
        Directory.CreateDirectory(_root);
        Run("nas.local").ShouldBe(DeploymentInitCommand.Success);
        Read("tls", "ca.crt").ShouldNotBe(trusted);
        Directory.Delete(Path.Combine(_root, "ca"), recursive: true);
        CopyDirectory(backup, Path.Combine(_root, "ca"));
        Directory.Delete(backup, recursive: true);

        Run("nas.local").ShouldBe(DeploymentInitCommand.Success);

        Read("tls", "ca.crt").ShouldBe(trusted);
        using X509Certificate2 authority = X509Certificate2.CreateFromPem(trusted);
        using X509Certificate2 server = X509Certificate2.CreateFromPemFile(
            Path.Combine(_root, "tls", "fullchain.pem"), Path.Combine(_root, "tls", "privkey.pem"));
        Chains(server, authority).ShouldBeTrue();
        string reissued = Read("tls", "fullchain.pem");
        Run("nas.local").ShouldBe(DeploymentInitCommand.Success);
        Read("tls", "fullchain.pem").ShouldBe(reissued);
    }

    [Theory]
    [InlineData("ca.key")]
    [InlineData("ca.crt")]
    public void Run_IncompleteAuthority_FailsWithRecoveryInstructionsAndChangesNothing(string missing)
    {
        Run("nas.local").ShouldBe(DeploymentInitCommand.Success);
        string server = Read("tls", "fullchain.pem");
        File.Delete(Path.Combine(_root, "ca", missing));

        Run("nas.local").ShouldBe(DeploymentInitCommand.Failed);

        _errors.ToString().ShouldContain("Restore that directory from a backup, or remove it");
        Read("tls", "fullchain.pem").ShouldBe(server);
        Directory.GetFiles(Path.Combine(_root, "ca")).Length.ShouldBe(1);
    }

    [Fact]
    public void Run_CorruptAuthority_FailsWithRecoveryInstructionsAndChangesNothing()
    {
        Run("nas.local").ShouldBe(DeploymentInitCommand.Success);
        File.WriteAllText(Path.Combine(_root, "ca", "ca.key"), "not a key");

        Run("nas.local").ShouldBe(DeploymentInitCommand.Failed);

        _errors.ToString().ShouldContain("cannot be used");
        Read("ca", "ca.key").ShouldBe("not a key");
    }

    [Fact]
    public void Run_AuthorityIssuedUnderAnEarlierConstraintPolicy_IsReplaced()
    {
        // An authority for an address that constrains addresses only, as the first policy issued it.
        Run("192.168.1.50").ShouldBe(DeploymentInitCommand.Success);
        using (X509Certificate2 current = X509Certificate2.CreateFromPemFile(
                   Path.Combine(_root, "ca", "ca.crt"), Path.Combine(_root, "ca", "ca.key")))
        using (var key = System.Security.Cryptography.ECDsa.Create())
        {
            CertificateRequest request = new(current.SubjectName, key, System.Security.Cryptography.HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            // permittedSubtrees { iPAddress 192.168.1.50/32 } and nothing about DNS names.
            request.CertificateExtensions.Add(new X509Extension("2.5.29.30",
                Convert.FromHexString("300EA00C300A8708C0A80132FFFFFFFF"), critical: true));
            using X509Certificate2 flawed = request.CreateSelfSigned(Start.AddDays(-1), Start.AddDays(3650));
            using X509Certificate2 forged = Sign(flawed, "bank.example");
            Chains(forged, flawed).ShouldBeTrue("the earlier policy left DNS names unrestricted");
            File.WriteAllText(Path.Combine(_root, "ca", "ca.crt"), flawed.ExportCertificatePem());
            File.WriteAllText(Path.Combine(_root, "ca", "ca.key"), key.ExportPkcs8PrivateKeyPem());
        }

        string flawedAuthority = Read("ca", "ca.crt");

        Run("192.168.1.50").ShouldBe(DeploymentInitCommand.Success);

        Read("ca", "ca.crt").ShouldNotBe(flawedAuthority);
        _output.ToString().ShouldContain("Trust tls/ca.crt on each device");
        using X509Certificate2 replaced = X509Certificate2.CreateFromPemFile(
            Path.Combine(_root, "ca", "ca.crt"), Path.Combine(_root, "ca", "ca.key"));
        using X509Certificate2 refused = Sign(replaced, "bank.example");
        Chains(refused, replaced).ShouldBeFalse();
    }

    [Theory]
    [InlineData("fullchain.pem", "privkey.pem")]
    [InlineData("privkey.pem", "fullchain.pem")]
    public void Run_HalfOfASuppliedCertificate_FailsWithInstructionsAndWritesNothing(string present, string missing)
    {
        Directory.CreateDirectory(Path.Combine(_root, "tls"));
        File.WriteAllText(Path.Combine(_root, "tls", present), "operator material");

        Run("nas.local").ShouldBe(DeploymentInitCommand.Failed);

        _errors.ToString().ShouldContain($"tls/{present} exists without tls/{missing}");
        _errors.ToString().ShouldContain("Supply both files");
        Read("tls", present).ShouldBe("operator material");
        Directory.GetFiles(Path.Combine(_root, "tls")).Length.ShouldBe(1);
        Directory.Exists(Path.Combine(_root, "ca")).ShouldBeFalse();
    }

    [Fact]
    public void Run_ServerCertificateLost_ReissuesUnderTheSameAuthority()
    {
        Run("nas.local").ShouldBe(DeploymentInitCommand.Success);
        string authority = Read("tls", "ca.crt");
        Directory.Delete(Path.Combine(_root, "tls"), recursive: true);

        Run("nas.local").ShouldBe(DeploymentInitCommand.Success);

        Read("tls", "ca.crt").ShouldBe(authority);
        File.Exists(Path.Combine(_root, "tls", "fullchain.pem")).ShouldBeTrue();
    }

    [Fact]
    public void Run_OperatorCertificateBesideAnEarlierGeneratedAuthority_IsNeverReplaced()
    {
        Run("nas.local").ShouldBe(DeploymentInitCommand.Success);
        using X509Certificate2 other = Sign(SelfSigned("CN=Operator authority"), "nas.local");
        File.WriteAllText(Path.Combine(_root, "tls", "fullchain.pem"), other.ExportCertificatePem());
        File.WriteAllText(Path.Combine(_root, "tls", "privkey.pem"), "operator key");

        Run("nas.local").ShouldBe(DeploymentInitCommand.Success);

        Read("tls", "fullchain.pem").ShouldBe(other.ExportCertificatePem());
        Read("tls", "privkey.pem").ShouldBe("operator key");
        _output.ToString().ShouldContain("using the supplied certificate");
    }

    [Fact]
    public void AddRomdSecretFiles_ConfiguredDirectoryMissing_FailsInsteadOfStartingWithoutSecrets()
    {
        new ConfigurationBuilder().AddRomdSecretFiles(null).Build().AsEnumerable().ShouldBeEmpty();

        Should.Throw<DirectoryNotFoundException>(() =>
            new ConfigurationBuilder().AddRomdSecretFiles(Path.Combine(_root, "missing")).Build());
    }

    private static X509Certificate2 SelfSigned(string subject)
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        CertificateRequest request = new(subject, key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(Start.AddDays(-1), Start.AddDays(30));
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static X509Certificate2 Sign(X509Certificate2 authority, string name)
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        // As ROMD issues them: the name is an alternative name only, never the common name.
        CertificateRequest request = new("CN=ROMD test certificate", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder alternatives = new();
        if (IPAddress.TryParse(name, out IPAddress? address))
            alternatives.AddIpAddress(address);
        else
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
