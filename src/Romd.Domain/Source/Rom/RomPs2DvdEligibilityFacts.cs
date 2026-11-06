using Romd.Domain.Hashing;

namespace Romd.Domain.Source.Rom;

public enum RomDvdMediaKind { Dvd5, Dvd9 }

/// <summary>Observed root ISO boot layout, never physical DVD media proof.</summary>
public sealed record RomPs2BootLayoutFacts : RomLayoutObservation
{
    public const string ProfileVersion = "ps2-iso-root-elf/v1";
    public const long MaximumLogicalBytes = 10L << 30;

    public RomPs2BootLayoutFacts(long logicalBytes, uint volumeBlocks, uint primaryDescriptorBlock,
        uint rootDirectoryBlock, uint rootDirectoryBytes, uint systemCnfBlock, uint systemCnfBytes,
        uint bootExecutableBlock, uint bootExecutableBytes, string observedBoot2Value, string bootPath,
        string bootSerial, uint entryPoint, ushort programHeaderCount, string profile, string inspectorBuild)
    {
        if (logicalBytes is <= 0 or > MaximumLogicalBytes || logicalBytes % 2048 != 0 ||
            primaryDescriptorBlock is < 16 or >= 80 || volumeBlocks < primaryDescriptorBlock + 2 ||
            (ulong)volumeBlocks * 2048 > (ulong)logicalBytes || rootDirectoryBytes is 0 or > (256 << 10) ||
            systemCnfBytes is 0 or > (16 << 10) || bootExecutableBytes < 84 ||
            entryPoint == 0 || entryPoint % 4 != 0 || programHeaderCount is < 1 or > 128 ||
            (ulong)52 + (ulong)programHeaderCount * 32 > bootExecutableBytes)
            throw new ArgumentException("PS2 boot layout requires bounded observed ISO and executable facts.");
        ulong volumeBytes = (ulong)volumeBlocks * 2048;
        if (!Inside(rootDirectoryBlock, rootDirectoryBytes) || !Inside(systemCnfBlock, systemCnfBytes) ||
            !Inside(bootExecutableBlock, bootExecutableBytes) ||
            Overlaps(rootDirectoryBlock, rootDirectoryBytes, systemCnfBlock, systemCnfBytes) ||
            Overlaps(rootDirectoryBlock, rootDirectoryBytes, bootExecutableBlock, bootExecutableBytes) ||
            Overlaps(systemCnfBlock, systemCnfBytes, bootExecutableBlock, bootExecutableBytes))
            throw new ArgumentException("PS2 boot extents must be contained, disjoint observed volume ranges.");
        if (!ValidSerial(bootSerial) || bootPath != "cdrom0:\\" + bootSerial + ";1" ||
            !string.Equals(observedBoot2Value, bootPath, StringComparison.OrdinalIgnoreCase) || profile != ProfileVersion)
            throw new ArgumentException("PS2 root boot facts require their exact bounded normalized serial path and profile.");
        ValidateLabel(inspectorBuild, 256);
        LogicalBytes = logicalBytes;
        VolumeBlocks = volumeBlocks;
        PrimaryDescriptorBlock = primaryDescriptorBlock;
        RootDirectoryBlock = rootDirectoryBlock;
        RootDirectoryBytes = rootDirectoryBytes;
        SystemCnfBlock = systemCnfBlock;
        SystemCnfBytes = systemCnfBytes;
        BootExecutableBlock = bootExecutableBlock;
        BootExecutableBytes = bootExecutableBytes;
        ObservedBoot2Value = observedBoot2Value;
        BootPath = bootPath;
        BootSerial = bootSerial;
        EntryPoint = entryPoint;
        ProgramHeaderCount = programHeaderCount;
        Profile = profile;
        InspectorBuild = inspectorBuild;

        bool Inside(uint block, uint bytes) => block > primaryDescriptorBlock + 1 &&
            (ulong)block * 2048 < volumeBytes && bytes <= volumeBytes - (ulong)block * 2048;
    }

    public long LogicalBytes { get; }
    public uint VolumeBlocks { get; }
    public uint PrimaryDescriptorBlock { get; }
    public uint RootDirectoryBlock { get; }
    public uint RootDirectoryBytes { get; }
    public uint SystemCnfBlock { get; }
    public uint SystemCnfBytes { get; }
    public uint BootExecutableBlock { get; }
    public uint BootExecutableBytes { get; }
    public string ObservedBoot2Value { get; }
    public string BootPath { get; }
    public string BootSerial { get; }
    public uint EntryPoint { get; }
    public ushort ProgramHeaderCount { get; }
    public string Profile { get; }
    public string InspectorBuild { get; }
    public override bool Describes(RomLogicalIdentity identity) => LogicalBytes == identity.Size;

    private static bool Overlaps(uint first, uint firstBytes, uint second, uint secondBytes) =>
        (ulong)first * 2048 < (ulong)second * 2048 + secondBytes &&
        (ulong)second * 2048 < (ulong)first * 2048 + firstBytes;

    private static bool ValidSerial(string? serial) => serial is { Length: 11 } && serial[4] == '_' && serial[8] == '.' &&
        serial.Take(4).All(c => c is >= 'A' and <= 'Z') &&
        new[] { 5, 6, 7, 9, 10 }.All(index => char.IsAsciiDigit(serial[index]));

    private static void ValidateLabel(string value, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > limit || value.Any(char.IsControl))
            throw new ArgumentException("Invalid boot inspector label.");
    }
}

/// <summary>Caller-trusted independent DVD reference; construction validates binding, not reference authenticity.</summary>
/// <remarks>Published legacy hashes/length and separately measured SHA256 identify logical original bytes.
/// The reference profile/hash/source must name durable provenance, never a filename-derived media kind.</remarks>
public sealed record RomDvdMediaReference
{
    public RomDvdMediaReference(Guid referenceRecordId, Sha256 contentSha256, long contentSize, Sha1 sha1, Md5 md5, Crc32 crc32,
        RomDvdMediaKind mediaKind, string profile, Sha256 referenceSha256, string referenceSource)
    {
        if (referenceRecordId == Guid.Empty || contentSha256.IsEmpty || sha1.IsEmpty || md5.IsEmpty || crc32.IsEmpty || referenceSha256.IsEmpty ||
            contentSize is <= 0 or > RomPs2BootLayoutFacts.MaximumLogicalBytes || contentSize % 2048 != 0 || !Enum.IsDefined(mediaKind))
            throw new ArgumentException("Independent DVD reference requires complete exact logical identity and DVD media kind.");
        ValidateLabel(profile, 128);
        ValidateLabel(referenceSource, 256);
        ReferenceRecordId = referenceRecordId;
        ContentSha256 = contentSha256;
        ContentSize = contentSize;
        Sha1 = sha1;
        Md5 = md5;
        Crc32 = crc32;
        MediaKind = mediaKind;
        Profile = profile;
        ReferenceSha256 = referenceSha256;
        ReferenceSource = referenceSource;
    }

    public Sha256 ContentSha256 { get; }
    public long ContentSize { get; }
    public Sha1 Sha1 { get; }
    public Md5 Md5 { get; }
    public Crc32 Crc32 { get; }
    public Guid ReferenceRecordId { get; }
    public RomDvdMediaKind MediaKind { get; }
    public string Profile { get; }
    public Sha256 ReferenceSha256 { get; }
    public string ReferenceSource { get; }

    private static void ValidateLabel(string value, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > limit || value.Any(char.IsControl))
            throw new ArgumentException("Invalid independent reference label.");
    }
}

/// <summary>Immutable PS2 eligibility details require both observed boot layout and an independently trusted DVD reference.</summary>
public sealed record RomPs2DvdEligibilityFacts
{
    public const string ProfileVersion = "ps2-dvd-eligibility/v1";

    public RomPs2DvdEligibilityFacts(Sha256 contentSha256, long contentSize, Sha1 sha1, Md5 md5, Crc32 crc32,
        RomPs2BootLayoutFacts boot, RomDvdMediaReference dvdReference)
    {
        ArgumentNullException.ThrowIfNull(boot);
        ArgumentNullException.ThrowIfNull(dvdReference);
        if (contentSha256.IsEmpty || sha1.IsEmpty || md5.IsEmpty || crc32.IsEmpty ||
            contentSize != boot.LogicalBytes || contentSha256 != dvdReference.ContentSha256 ||
            contentSize != dvdReference.ContentSize || sha1 != dvdReference.Sha1 || md5 != dvdReference.Md5 || crc32 != dvdReference.Crc32)
            throw new ArgumentException("PS2 DVD eligibility must bind boot and independent reference to complete logical identity.");
        ContentSha256 = contentSha256;
        ContentSize = contentSize;
        Sha1 = sha1;
        Md5 = md5;
        Crc32 = crc32;
        Boot = boot;
        DvdReference = dvdReference;
    }

    public Sha256 ContentSha256 { get; }
    public long ContentSize { get; }
    public Sha1 Sha1 { get; }
    public Md5 Md5 { get; }
    public Crc32 Crc32 { get; }
    public RomPs2BootLayoutFacts Boot { get; }
    public RomDvdMediaReference DvdReference { get; }
}
