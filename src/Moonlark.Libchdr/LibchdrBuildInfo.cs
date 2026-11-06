namespace Moonlark.Libchdr;

/// <summary>The immutable identity of the validated resident native library.</summary>
public sealed class LibchdrBuildInfo
{
    internal LibchdrBuildInfo(string familyVersion, string upstreamVersion, string upstreamCommit, string buildId)
    {
        FamilyVersion = familyVersion;
        UpstreamVersion = upstreamVersion;
        UpstreamCommit = upstreamCommit;
        BuildId = buildId;
    }

    /// <summary>The exact paired managed/native package family version.</summary>
    public string FamilyVersion { get; }

    /// <summary>The upstream git-describe identity.</summary>
    public string UpstreamVersion { get; }

    /// <summary>The exact upstream source commit.</summary>
    public string UpstreamCommit { get; }

    /// <summary>The SHA-256 identity of the native build recipe, distinct from its binary digest.</summary>
    public string BuildId { get; }

    /// <summary>Whether decoding preserves raw data sectors.</summary>
    public bool HasRawSectors => true;

    /// <summary>Whether decoding preserves subcode.</summary>
    public bool HasSubcode => true;

    /// <summary>Whether the native build checks stored per-block CRCs when the CHD supplies them.</summary>
    public bool VerifiesBlockCrc => true;
}
