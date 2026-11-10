namespace Romd.Domain.Source.Rom;

/// <summary>Platform-specific observations of independently measured first-track bytes.</summary>
public abstract record RomCdBoot
{
    private protected RomCdBoot(string profile, long firstTrackIndex01)
    {
        Profile = profile;
        FirstTrackIndex01 = firstTrackIndex01;
    }
    public string Profile { get; }
    public long FirstTrackIndex01 { get; }
}
