using System.Buffers.Binary;
using Romd.Media.Nintendo;
using Romd.Storage;

namespace Romd.Media.Rvz;

/// <summary>Format-specific RVZ rules shared by worker execution and its host adapters.</summary>
public abstract class RvzCodecFamily
{
    private RvzCodecFamily() { }

    public static RvzCodecFamily GameCube { get; } = new GameCubeFamily();
    public static RvzCodecFamily Wii { get; } = new WiiFamily();
    public static RvzCodecFamily? For(NintendoDiscKind kind) => kind switch
    {
        NintendoDiscKind.GameCube => GameCube,
        NintendoDiscKind.Wii => Wii,
        _ => null
    };
    public static RvzCodecFamily? ForWorkerArgument(string? argument) =>
        argument == GameCube.WorkerArgument ? GameCube : argument == Wii.WorkerArgument ? Wii : null;

    public abstract NintendoDiscKind DiscKind { get; }
    public abstract string WorkerArgument { get; }
    public abstract string EncoderBuild { get; }
    public abstract uint ContainerDiscType { get; }
    public abstract bool HasWiiPartitions { get; }
    public abstract bool MatchesHeader(ReadOnlySpan<byte> prefix);
    public abstract bool MatchesLayout(int partitions);
    public abstract RvzCanonicalOutputBound GetOutputBound(long logicalBytes, long maxLogicalBytes, CancellationToken ct = default);
    public abstract Task<RvzEncodeResult> EncodeAsync(IRandomAccessContent source, Stream output,
        long maxLogicalBytes, long maxOutputBytes, CancellationToken ct = default);

    private sealed class GameCubeFamily : RvzCodecFamily
    {
        public override NintendoDiscKind DiscKind => NintendoDiscKind.GameCube;
        public override string WorkerArgument => "gamecube";
        public override string EncoderBuild => RvzEncoder.EncoderBuild;
        public override uint ContainerDiscType => 1;
        public override bool HasWiiPartitions => false;
        public override bool MatchesHeader(ReadOnlySpan<byte> prefix) => prefix.Length == 32 &&
            BinaryPrimitives.ReadUInt32BigEndian(prefix[0x1c..]) == 0xc2339f3dU;
        public override bool MatchesLayout(int partitions) => partitions == 0;
        public override RvzCanonicalOutputBound GetOutputBound(long logicalBytes, long maxLogicalBytes, CancellationToken ct = default) =>
            RvzCanonicalBounds.GetGameCubeOutputBound(logicalBytes, maxLogicalBytes, ct);
        public override Task<RvzEncodeResult> EncodeAsync(IRandomAccessContent source, Stream output,
            long maxLogicalBytes, long maxOutputBytes, CancellationToken ct = default) =>
            new RvzEncoder().EncodeGameCubeAsync(source, output, RvzCanonicalBounds.GameCubeOptions(maxLogicalBytes, maxOutputBytes), ct);
    }

    private sealed class WiiFamily : RvzCodecFamily
    {
        public override NintendoDiscKind DiscKind => NintendoDiscKind.Wii;
        public override string WorkerArgument => "wii";
        public override string EncoderBuild => RvzWiiEncoder.EncoderBuild;
        public override uint ContainerDiscType => 2;
        public override bool HasWiiPartitions => true;
        public override bool MatchesHeader(ReadOnlySpan<byte> prefix) => prefix.Length == 32 &&
            BinaryPrimitives.ReadUInt32BigEndian(prefix[0x18..]) == 0x5d1c9ea3U;
        public override bool MatchesLayout(int partitions) => partitions > 0;
        public override RvzCanonicalOutputBound GetOutputBound(long logicalBytes, long maxLogicalBytes, CancellationToken ct = default) =>
            RvzCanonicalBounds.GetWiiOutputBound(logicalBytes, maxLogicalBytes, ct);
        public override Task<RvzEncodeResult> EncodeAsync(IRandomAccessContent source, Stream output,
            long maxLogicalBytes, long maxOutputBytes, CancellationToken ct = default) =>
            new RvzWiiEncoder().EncodeAsync(source, output, RvzCanonicalBounds.WiiOptions(maxLogicalBytes, maxOutputBytes), ct);
    }
}
