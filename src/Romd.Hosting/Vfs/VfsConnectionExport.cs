using System.Security.Cryptography;
using System.Text;
using Romd.Consumer.Application.Vfs;
using Romd.Contracts.Consumer.Account;

namespace Romd.Hosting.Vfs;

public sealed class VfsConnectionExport
{
    private readonly IConfiguration configuration;
    private readonly ConsumerVfsOptions options;

    public VfsConnectionExport(IConfiguration configuration, ConsumerVfsOptions options)
    {
        this.configuration = configuration;
        this.options = options;
    }

    public string? Endpoint
    {
        get
        {
            string? origin = configuration["Romd:ConsumerHost:PublicUrl"];
            if (!options.Enabled || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0)
                return null;
            return new Uri(uri, "/vfs/library/").AbsoluteUri;
        }
    }

    public VfsConnectionCredentialDto Build(IssuedVfsCredential issued, string endpoint) => new(
        issued.Connection, issued.Password, endpoint,
        $"[romd]\ntype = webdav\nurl = {endpoint}\nvendor = owncloud\nuser = {issued.Connection.Username}\npass = {Obscure(issued.Password, RandomNumberGenerator.GetBytes(16))}\n");

    // rclone's publicly known key and AES-CTR encoding, NOT secret storage or encryption protection.
    // Protocol: https://github.com/rclone/rclone/blob/master/fs/config/obscure/obscure.go
    internal static string Obscure(string password, ReadOnlySpan<byte> iv)
    {
        if (iv.Length != 16)
            throw new ArgumentException("The rclone IV must contain 16 bytes.", nameof(iv));
        using var aes = Aes.Create();
        aes.Key = Convert.FromHexString("9c935b48730a554d6bfd7c63c886a92bd390198eb8128afbf4de162b8b95f638");
        byte[] plaintext = Encoding.UTF8.GetBytes(password);
        byte[] output = new byte[16 + plaintext.Length];
        iv.CopyTo(output.AsSpan(0, 16));
        byte[] counter = output[..16];
        Span<byte> block = stackalloc byte[16];
        for (int offset = 0; offset < plaintext.Length; offset += 16)
        {
            aes.EncryptEcb(counter, block, PaddingMode.None);
            for (int index = 0; index < Math.Min(16, plaintext.Length - offset); index++)
                output[16 + offset + index] = (byte)(plaintext[offset + index] ^ block[index]);
            for (int index = 15; index >= 0 && ++counter[index] == 0; index--)
            { }
        }
        return Convert.ToBase64String(output).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
