using Romd.Domain.Source.Rom;

namespace Romd.Application.Common.Content;

/// <summary>
///     What this host's deployment can do with media. Capabilities are deployment state, not policy: they come from the
///     native builds verified at startup and from the runtime each media kind's registration qualifies, and every answer is
///     computed when asked, so an invalidated build stops answering at once. Encode capability never decides whether an
///     existing artifact is readable; read admission asks only about decoding.
/// </summary>
public interface IMediaHostCapabilities
{
    /// <summary>The verified build this host decodes an encoding with; null when it needs no native build or none is ready.</summary>
    RomChdDecoderBuild? Decoder(RomRepresentationEncoding encoding);

    /// <summary>The resident managed decoder build used for RVZ measurements and reads.</summary>
    string ManagedDecoderBuild { get; }

    /// <summary>Whether this host can read stored artifacts of the kind: its decoder build is ready and qualified for it.</summary>
    bool CanDecode(string kind);

    /// <summary>
    ///     Whether this host can encode the kind: it runs media encoding, and its encoder and decoder builds are ready and
    ///     qualified for it. Only the worker encodes.
    /// </summary>
    bool CanEncode(string kind);

    /// <summary>The runtime identifier the kind's native builds are qualified on; null when every verified build qualifies.</summary>
    string? QualifiedRuntime(string kind);

    /// <summary>Whether a native build for the runtime, this host's or one recorded with an artifact, is qualified for the kind.</summary>
    bool Qualifies(string kind, string runtimeIdentifier);
}
