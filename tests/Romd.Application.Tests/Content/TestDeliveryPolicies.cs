using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Application.Tests.Content;

/// <summary>Mirrors the production composition, which <c>RomMediaPolicyTests</c> pins in Infrastructure tests.</summary>
internal static class TestDeliveryPolicies
{
    public static RomDeliveryFormatCatalog Formats { get; } = new([
        new(RomDeliveryFormat.Original, "Original", null), new(RomDeliveryFormat.Rvz, "RVZ", ".rvz"),
        new(RomDeliveryFormat.Chd, "CHD", ".chd")]);

    public static RomDeliveryPolicy Production { get; } = new(Formats, TestMediaKinds.Production);

    /// <summary>The production kinds with no runtime claimed, as planning behaved without a delivery policy.</summary>
    public static RomDeliveryPolicy WithoutRuntimes { get; } = new(Formats, TestMediaKinds.WithRuntimes(_ => []));
}
