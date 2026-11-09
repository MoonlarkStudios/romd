namespace Romd.Domain.Source.Rom;

/// <summary>Bounded observed PS1 root executable, distinct from catalog identity and emulator compatibility.</summary>
public sealed record RomPs1CdBoot : RomCdBoot
{
    public const string CurrentProfile = "ps1-cd-root-exe/v1";
    public RomPs1CdBoot(string profile, long firstTrackIndex01, uint volumeBlocks, uint rootBlock, uint rootBytes,
        string bootPath, uint bootBlock, uint bootBytes, uint entryPoint, uint loadAddress, uint loadBytes) : base(profile, firstTrackIndex01)
    {
        if (profile != CurrentProfile || firstTrackIndex01 is < 0 or > 450000 || volumeBlocks is < 17 or > 450000 ||
            rootBlock < 17 || rootBlock >= volumeBlocks || rootBytes is < 1 or > (256 << 10) ||
            (ulong)rootBlock * 2048 + rootBytes > (ulong)volumeBlocks * 2048 || bootBlock < 17 || bootBlock >= volumeBlocks ||
            bootPath is not { Length: > 0 and <= 66 } || bootPath.Any(c => char.IsControl(c) || "/\\".Contains(c)) ||
            bootBytes < 2048 || (ulong)bootBlock * 2048 + bootBytes > (ulong)volumeBlocks * 2048 ||
            loadBytes is < 1 or > (2 << 20) || bootBytes < 2048UL + loadBytes || entryPoint % 4 != 0 || !Ram(entryPoint, 4) || !Ram(loadAddress, loadBytes))
            throw new ArgumentException("Incomplete or unsupported PS1 boot observation.");
        VolumeBlocks = volumeBlocks;
        RootBlock = rootBlock;
        RootBytes = rootBytes;
        BootPath = bootPath;
        BootBlock = bootBlock;
        BootBytes = bootBytes;
        EntryPoint = entryPoint;
        LoadAddress = loadAddress;
        LoadBytes = loadBytes;
    }
    public uint VolumeBlocks { get; }
    public uint RootBlock { get; }
    public uint RootBytes { get; }
    public string BootPath { get; }
    public uint BootBlock { get; }
    public uint BootBytes { get; }
    public uint EntryPoint { get; }
    public uint LoadAddress { get; }
    public uint LoadBytes { get; }
    private static bool Ram(uint address, uint size) => address >> 24 is 0x80 or 0xa0 && (ulong)(address & 0x00ffffff) + size <= (2 << 20);
}

