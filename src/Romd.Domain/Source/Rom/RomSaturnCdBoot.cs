using Romd.Domain.Hashing;

namespace Romd.Domain.Source.Rom;

/// <summary>A bounded Saturn initial-program header; catalog identity and gameplay are separate claims.</summary>
public sealed record RomSaturnCdBoot : RomCdBoot
{
    public const string CurrentProfile = "saturn-cd-ip/v1";
    public RomSaturnCdBoot(string profile, long firstTrackIndex01, string productCode, string areaSymbols,
        uint initialProgramBytes, Sha256 headerSha256) : base(profile, firstTrackIndex01)
    {
        if (profile != CurrentProfile || firstTrackIndex01 is < 0 or > 450000 ||
            productCode is not { Length: > 0 and <= 10 } || productCode.Any(c => c is < '!' or > '~') ||
            areaSymbols is not { Length: > 0 and <= 16 } || areaSymbols.Any(c => !"JTUBKAEL ".Contains(c)) ||
            areaSymbols.All(c => c == ' ') || initialProgramBytes is < 256 or > 32768 || headerSha256.IsEmpty)
            throw new ArgumentException("Incomplete or unsupported Saturn initial-program observation.");
        ProductCode = productCode;
        AreaSymbols = areaSymbols;
        InitialProgramBytes = initialProgramBytes;
        HeaderSha256 = headerSha256;
    }
    public string ProductCode { get; }
    public string AreaSymbols { get; }
    public uint InitialProgramBytes { get; }
    public Sha256 HeaderSha256 { get; }
}
