using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>Explicit retention authority acquired before publishing staged bytes. An incoming artifact's proof can only extend those same bytes.</summary>
public abstract class RomCdArchivePublicationAuthority
{
    private RomCdArchivePublicationAuthority() { }
    public sealed class Keep : RomCdArchivePublicationAuthority
    {
        public Keep(RomRepresentationFacts retained) { ArgumentNullException.ThrowIfNull(retained); Retained = retained; }
        public RomRepresentationFacts Retained { get; }
    }
    public sealed class Publish : RomCdArchivePublicationAuthority
    {
        public Publish(IReadOnlyDictionary<string, int> binRomIds, RomRepresentationFacts? retained)
        {
            ArgumentNullException.ThrowIfNull(binRomIds);
            BinRomIds = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(binRomIds.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
            Retained = retained;
        }
        public IReadOnlyDictionary<string, int> BinRomIds { get; }
        public RomRepresentationFacts? Retained { get; }
    }
}
