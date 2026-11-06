namespace Romd.Domain.Source.Rom;

/// <summary>Immutable layout facts, independent of the artifact's decoder measurements.</summary>
public abstract class RomReconstructionManifest
{
    private RomReconstructionManifest() { }

    public abstract bool ContainsOutput(RomOutputSelector selector);

    /// <summary>The implicit layout for one artifact that reconstructs one file.</summary>
    public sealed class WholeFile : RomReconstructionManifest
    {
        public static WholeFile Instance { get; } = new();
        private WholeFile() { }
        public override bool ContainsOutput(RomOutputSelector selector) => selector is RomOutputSelector.Whole;
    }

    /// <summary>Complete raw-track placement and exact member identities; no decoder or catalog preservation approval is implied.</summary>
    public sealed class CdTracks : RomReconstructionManifest
    {
        public const int MaximumMembers = 199;

        public CdTracks(long logicalFrames, IReadOnlyList<RomCdTrack> tracks, IReadOnlyList<RomCdFileMember> members)
        {
            ArgumentNullException.ThrowIfNull(tracks);
            ArgumentNullException.ThrowIfNull(members);
            // Validate the captured collections, so caller mutation cannot change a checked layout.
            var capturedTracks = tracks.ToArray();
            var capturedMembers = members.ToArray();
            if (logicalFrames is < 1 or > 450000 || capturedTracks.Length is < 1 or > 99 ||
                capturedMembers.Length is < 2 or > MaximumMembers ||
                capturedTracks.Any(track => track is null) || capturedMembers.Any(member => member is null))
                throw new ArgumentException("A CD layout requires bounded tracks and exact members.");
            long nextFrame = 0;
            for (int index = 0; index < capturedTracks.Length; index++)
            {
                var track = capturedTracks[index];
                if (track.Number != index + 1 || track.FirstFrame != nextFrame || track.Frames is < 1 or > 450000 ||
                    !Enum.IsDefined(track.Kind))
                    throw new ArgumentException("CD tracks must be ordered, bounded and include only four-frame CHD alignment gaps.");
                nextFrame = checked(nextFrame + ((track.Frames + 3L) / 4 * 4));
            }
            if (nextFrame != logicalFrames ||
                capturedMembers.Select(member => member.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != capturedMembers.Length ||
                capturedMembers.OfType<RomCdStoredMember>().Count(member => member.Role == RomCdStoredRole.Cue) != 1 ||
                capturedMembers.Any(member => member is not (RomCdBinMember or RomCdStoredMember)))
                throw new ArgumentException("A CD layout requires complete geometry, unique paths and one retained CUE.");
            var bins = capturedMembers.OfType<RomCdBinMember>().ToArray();
            if (!bins.SelectMany(member => member.Tracks).Order().SequenceEqual(Enumerable.Range(1, capturedTracks.Length)))
                throw new ArgumentException("Every track must belong to exactly one original BIN.");
            foreach (var bin in bins)
                if (bin.Identity.Size != bin.Tracks.Sum(number => (long)capturedTracks[number - 1].Frames * 2352))
                    throw new ArgumentException("BIN identity size disagrees with its whole-track reconstruction recipe.");
            LogicalFrames = logicalFrames;
            Tracks = Array.AsReadOnly(capturedTracks);
            Members = Array.AsReadOnly(capturedMembers);
        }

        public long LogicalFrames { get; }
        public IReadOnlyList<RomCdTrack> Tracks { get; }
        public IReadOnlyList<RomCdFileMember> Members { get; }
        public override bool ContainsOutput(RomOutputSelector selector) => selector is RomOutputSelector.Member member &&
            member.Ordinal < Members.Count && Members[member.Ordinal] is RomCdBinMember;
    }
}

/// <summary>Selects one reconstructed output. A member never grants access to its container or siblings.</summary>
public abstract class RomOutputSelector : IEquatable<RomOutputSelector>
{
    private RomOutputSelector() { }

    public bool Equals(RomOutputSelector? other) => (this, other) switch
    {
        (Whole, Whole) => true,
        (Member left, Member right) => left.Ordinal == right.Ordinal,
        _ => false
    };
    public sealed override bool Equals(object? other) => other is RomOutputSelector selector && Equals(selector);
    public sealed override int GetHashCode() => this is Member member
        ? HashCode.Combine(typeof(Member), member.Ordinal)
        : typeof(Whole).GetHashCode();
    public static bool operator ==(RomOutputSelector? left, RomOutputSelector? right) =>
        ReferenceEquals(left, right) || left?.Equals(right) == true;
    public static bool operator !=(RomOutputSelector? left, RomOutputSelector? right) => !(left == right);

    public sealed class Whole : RomOutputSelector
    {
        public static Whole Instance { get; } = new();
        private Whole() { }
    }

    public sealed class Member : RomOutputSelector
    {
        public Member(int ordinal)
        {
            if (ordinal is < 0 or >= RomReconstructionManifest.CdTracks.MaximumMembers)
                throw new ArgumentOutOfRangeException(nameof(ordinal));
            Ordinal = ordinal;
        }
        public int Ordinal { get; }
    }
}
