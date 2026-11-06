using Romd.Application.Common.Content;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Consumer.Application.Delivery;

public sealed record ConsumerReleaseManifestRequest(
    ConsumerLibraryScope Scope,
    int ReleaseId);

public sealed record ConsumerReleaseManifest(
    Guid UserId,
    int TitleId,
    int PlatformId,
    string PlatformShortName,
    int ReleaseId,
    string ReleaseName,
    string? Revision,
    bool IsComplete,
    ConsumerReleaseRuntime Runtime,
    IReadOnlyList<ConsumerReleaseManifestItem> Items)
{
    public bool IsDeliveryComplete => Items.Count > 0 && Items.All(item => item.IsAvailable);
}

public sealed record ConsumerReleaseRuntime(
    string ContentType,
    ConsumerLaunchTarget? Launch,
    string Packaging,
    long MinimumInstallBytes);

public sealed record ConsumerLaunchTarget(
    string Type,
    string RelativePath);

public abstract class ConsumerReleaseManifestSource
{
    private ConsumerReleaseManifestSource() { }
    public sealed class Missing : ConsumerReleaseManifestSource { }
    public sealed class Original : ConsumerReleaseManifestSource
    {
        public Original(RomLogicalIdentity identity, IReadOnlyList<ConsumerReleaseManifestFormat> formats)
        {
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentNullException.ThrowIfNull(formats);
            if (formats.Select(format => format.Delivery.Format).Distinct().Count() != formats.Count)
                throw new ArgumentException("Each original delivery format must occur once.", nameof(formats));
            Identity = identity;
            Formats = Array.AsReadOnly(formats.ToArray());
        }
        public RomLogicalIdentity Identity { get; }
        public IReadOnlyList<ConsumerReleaseManifestFormat> Formats { get; }
        public RomDeliveryTarget? AvailableTarget => Formats.SingleOrDefault(format => format.Delivery.Format == RomDeliveryFormat.Original &&
            format.Delivery.State == RomDeliveryState.Available && format.Delivery.Target is { } target && RomDeliveryTargets.IsValid(target))?.Delivery.Target;
    }
    public sealed class Derived : ConsumerReleaseManifestSource
    {
        public Derived(DerivedDescriptorBinding descriptor)
        {
            if (!ConsumerDerivedDescriptors.IsValid(descriptor))
                throw new ArgumentException("An exact captured derived descriptor is required.", nameof(descriptor));
            Descriptor = descriptor;
        }
        public DerivedDescriptorBinding Descriptor { get; }
    }
}
public sealed record ConsumerReleaseManifestItem(string RelativePath, string Role, long ExpectedSizeBytes, ConsumerReleaseManifestSource Source)
{
    public bool IsAvailable => Source is ConsumerReleaseManifestSource.Derived || Source is ConsumerReleaseManifestSource.Original { AvailableTarget: not null };
    public Sha256? Sha256 => Source switch
    {
        ConsumerReleaseManifestSource.Derived derived => derived.Descriptor.Identity.Sha256,
        ConsumerReleaseManifestSource.Original original => original.AvailableTarget?.Sha256,
        ConsumerReleaseManifestSource.Missing => null,
        _ => throw new InvalidOperationException("Unknown release delivery source.")
    };
    public long SizeBytes => Source switch
    {
        ConsumerReleaseManifestSource.Derived derived => derived.Descriptor.Identity.Size,
        ConsumerReleaseManifestSource.Original original => original.Identity.Size,
        ConsumerReleaseManifestSource.Missing => ExpectedSizeBytes,
        _ => throw new InvalidOperationException("Unknown release delivery source.")
    };
    public IReadOnlyList<ConsumerReleaseManifestFormat> Formats => Source is ConsumerReleaseManifestSource.Original original ? original.Formats : [];
}

public sealed record ConsumerReleaseManifestFormat(string RelativePath, RomDeliveryOption Delivery,
    ConsumerReleaseRuntime? Runtime = null);

public interface IConsumerReleaseManifestRepository
{
    Task<ConsumerLibraryReadResult<ConsumerReleaseManifest>> GetManifestAsync(
        ConsumerReleaseManifestRequest request,
        CancellationToken ct = default);
}
