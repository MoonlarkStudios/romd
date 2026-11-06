using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

/// <summary>Pure bounded prefix selection from encoding registrations. Resolves no verifier or service dependency.</summary>
public static class RomPreparationDetection
{
    public const int MaximumPrefixBytes = 16 * 2048 + 7;
    private const int _initialPrefixBytes = 4;
    private static IEnumerable<RomPreparationSignature> Signatures => RomEncodingComposition.Entries
        .Select(entry => entry.PreparationSignature).OfType<RomPreparationSignature>();

    internal static RomPreparationSignature Signature(RomRepresentationEncoding encoding) =>
        RomEncodingComposition.Entries.Single(entry => entry.Descriptor.Encoding == encoding).PreparationSignature
            ?? throw new InvalidOperationException("This encoding has no container signature.");

    public static int SelectPrefixLength(ReadOnlyMemory<byte> prefix, string filename)
    {
        var magic = prefix.Span;
        foreach (var signature in Signatures)
            if (magic.StartsWith(signature.Magic))
                return signature.PrefixBytes;
        foreach (var signature in Signatures)
            if (magic.Length == _initialPrefixBytes && OpensSignature(magic, signature.Magic))
                return signature.PrefixBytes;
        foreach (var signature in Signatures)
            if (signature.ClaimsFilename(filename))
                return signature.PrefixBytes;
        if (!IsIsoCandidate(filename))
            return _initialPrefixBytes;
        foreach (var signature in Signatures)
            // Exhausting a container magic is independent of how much header that encoding needs for admission.
            if (magic.Length >= signature.Magic.Length && OpensSignature(magic, signature.Magic))
                return Path.GetExtension(filename).Equals(".iso", StringComparison.OrdinalIgnoreCase) ? MaximumPrefixBytes : 32;
        if (prefix.Length < 32 || IsoPreparationAdapter.HasNintendoMagic(prefix.Span))
            return 32;
        return Path.GetExtension(filename).Equals(".iso", StringComparison.OrdinalIgnoreCase) ? MaximumPrefixBytes : 32;
    }
    private static bool OpensSignature(ReadOnlySpan<byte> magic, ReadOnlySpan<byte> signature) =>
        signature.Length > _initialPrefixBytes && magic.Length >= _initialPrefixBytes &&
        magic[.._initialPrefixBytes].SequenceEqual(signature[.._initialPrefixBytes]);
    internal static bool IsIsoCandidate(string filename) => Path.GetExtension(filename).ToLowerInvariant() is ".iso" or ".gcm" or ".wii";
}

/// <summary>Container routing metadata, declared once with its encoding. Prefix includes the fields needed for admission/hash selection.</summary>
internal sealed class RomPreparationSignature
{
    private readonly byte[] _magic;
    private readonly string _extension;
    public RomPreparationSignature(ReadOnlySpan<byte> magic, int prefixBytes, string extension)
    {
        if (magic.Length is < 4 or > 32 || prefixBytes < magic.Length || prefixBytes > RomPreparationDetection.MaximumPrefixBytes ||
            string.IsNullOrWhiteSpace(extension) || extension[0] != '.')
            throw new ArgumentException("A bounded signature, prefix and extension are required.");
        this._magic = magic.ToArray();
        PrefixBytes = prefixBytes;
        this._extension = extension;
    }
    public ReadOnlySpan<byte> Magic => _magic;
    public int PrefixBytes { get; }
    public bool ClaimsFilename(string filename) => Path.GetExtension(filename).Equals(_extension, StringComparison.OrdinalIgnoreCase);
}
