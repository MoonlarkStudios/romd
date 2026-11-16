using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Romd.Worker.Host;

/// <summary>
///     Issues the HTTPS certificate for an installation that brings none: a private certificate authority
///     limited to the installation's own names, and a server certificate signed by it. The authority's key
///     stays in <c>ca/</c>, which only initialization mounts; the ingress reads <c>tls/</c>.
/// </summary>
internal static class DeploymentCertificates
{
    internal const string AuthorityKey = "ca/ca.key";
    internal const string AuthorityCertificate = "ca/ca.crt";
    internal const string PublishedAuthority = "tls/ca.crt";
    internal const string ServerCertificate = "tls/fullchain.pem";
    internal const string ServerKey = "tls/privkey.pem";

    // Apple platforms reject privately issued server certificates valid for more than 825 days.
    internal static readonly TimeSpan ServerLifetime = TimeSpan.FromDays(825);
    internal static readonly TimeSpan RenewalWindow = TimeSpan.FromDays(30);
    private static readonly TimeSpan AuthorityLifetime = TimeSpan.FromDays(3650);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromDays(1);

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    // The ingress runs as a different user and is the only other service mounting tls/.
    private const UnixFileMode SharedRead = OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    internal enum Outcome
    {
        OperatorSupplied,
        Current,
        Renewed,
        Issued
    }

    internal static Outcome Ensure(string root, IReadOnlyCollection<string> hosts, DateTimeOffset now)
    {
        bool serverPresent = File.Exists(Path.Combine(root, ServerCertificate)) && File.Exists(Path.Combine(root, ServerKey));
        bool authorityPresent = File.Exists(Path.Combine(root, AuthorityKey)) && File.Exists(Path.Combine(root, AuthorityCertificate));
        if (serverPresent && !authorityPresent)
            return Outcome.OperatorSupplied;

        SortedSet<string> names = new(hosts.Select(Normalize), StringComparer.Ordinal);
        if (serverPresent && authorityPresent)
        {
            using X509Certificate2 server = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(root, ServerCertificate)));
            if (names.SetEquals(SubjectNames(server)))
            {
                if (server.NotAfter.ToUniversalTime() - now.UtcDateTime > RenewalWindow)
                    return Outcome.Current;
                using X509Certificate2 authority = X509Certificate2.CreateFromPemFile(
                    Path.Combine(root, AuthorityCertificate), Path.Combine(root, AuthorityKey));
                if (authority.NotAfter.ToUniversalTime() - now.UtcDateTime > ServerLifetime)
                {
                    WriteServer(root, authority, names, now);
                    return Outcome.Renewed;
                }
            }
        }

        // The authority is limited to these names, so a different name set (or an authority
        // too close to expiry to sign a full-lifetime certificate) needs a new one, which devices must trust again.
        using X509Certificate2 created = CreateAuthority(names, now);
        using ECDsa authorityKey = created.GetECDsaPrivateKey()!;
        DeploymentSecrets.Write(root, AuthorityKey, authorityKey.ExportPkcs8PrivateKeyPem(), OwnerOnly);
        DeploymentSecrets.Write(root, AuthorityCertificate, created.ExportCertificatePem(), OwnerOnly);
        DeploymentSecrets.Write(root, PublishedAuthority, created.ExportCertificatePem(), SharedRead);
        WriteServer(root, created, names, now);
        return Outcome.Issued;
    }

    private static string Normalize(string host) =>
        IPAddress.TryParse(host, out IPAddress? address) ? address.ToString() : host.ToLowerInvariant();

    private static IEnumerable<string> SubjectNames(X509Certificate2 certificate)
    {
        X509SubjectAlternativeNameExtension? names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().SingleOrDefault();
        return names is null
            ? []
            : names.EnumerateDnsNames().Concat(names.EnumerateIPAddresses().Select(address => address.ToString()));
    }

    private static X509Certificate2 CreateAuthority(IReadOnlyCollection<string> names, DateTimeOffset now)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN=ROMD local authority ({names.First()})", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(new X509Extension("2.5.29.30", NameConstraints(names), critical: true));
        return request.CreateSelfSigned(now - ClockSkew, now + AuthorityLifetime);
    }

    private static void WriteServer(string root, X509Certificate2 authority, IReadOnlyCollection<string> names, DateTimeOffset now)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={names.First()}", key, HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder alternatives = new();
        foreach (string name in names)
        {
            if (IPAddress.TryParse(name, out IPAddress? address))
                alternatives.AddIpAddress(address);
            else
                alternatives.AddDnsName(name);
        }

        request.CertificateExtensions.Add(alternatives.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
        using X509Certificate2 server = request.Create(
            authority, now - ClockSkew, now + ServerLifetime, RandomNumberGenerator.GetBytes(16));
        DeploymentSecrets.Write(root, ServerKey, key.ExportPkcs8PrivateKeyPem(), SharedRead);
        DeploymentSecrets.Write(root, ServerCertificate, server.ExportCertificatePem(), SharedRead);
    }

    /// <summary>RFC 5280 NameConstraints permitting exactly the given DNS names (and their subdomains) and addresses.</summary>
    private static byte[] NameConstraints(IReadOnlyCollection<string> names)
    {
        AsnWriter writer = new(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            foreach (string name in names)
            {
                using (writer.PushSequence())
                {
                    if (IPAddress.TryParse(name, out IPAddress? address))
                    {
                        byte[] bytes = address.GetAddressBytes();
                        // An address constraint is the address followed by an all-ones mask: this host only.
                        byte[] mask = Enumerable.Repeat((byte)0xFF, bytes.Length).ToArray();
                        writer.WriteOctetString([.. bytes, .. mask], new Asn1Tag(TagClass.ContextSpecific, 7));
                    }
                    else
                    {
                        writer.WriteCharacterString(UniversalTagNumber.IA5String, name, new Asn1Tag(TagClass.ContextSpecific, 2));
                    }
                }
            }
        }

        return writer.Encode();
    }

    internal static bool IsValidHost(string host) =>
        IPAddress.TryParse(host, out IPAddress? address)
            ? address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
            : Uri.CheckHostName(host) == UriHostNameType.Dns && host.Length <= 253;
}
