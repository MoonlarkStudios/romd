namespace Romd.Media.Rvz;

public enum RvzEncodeFailureKind { InvalidOptions, InvalidSource, UnsupportedMedia, LogicalLimitExceeded, OutputLimitExceeded }

/// <summary>Encoding creates an artifact; success is not verification or source-equivalence evidence.</summary>
public abstract record RvzEncodeResult
{
    private protected RvzEncodeResult() { }

    public sealed record Success(long ArtifactBytes, long LogicalBytes, int Groups, int PackedGroups,
        int CompressedGroups, int ZeroGroups, string EncoderBuild) : RvzEncodeResult;

    public sealed record Failure(RvzEncodeFailureKind Kind, string Description) : RvzEncodeResult;
}
