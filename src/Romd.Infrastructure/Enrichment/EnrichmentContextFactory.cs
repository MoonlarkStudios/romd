using Microsoft.Extensions.Options;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Platform;

namespace Romd.Infrastructure.Enrichment;

/// <summary>
///     Builds EnrichmentContext for a title by selecting representative evidence
///     and collecting owned ROM hashes.
/// </summary>
public sealed class EnrichmentContextFactory(
    ITitleEnrichmentEvidenceReader evidenceReader,
    IOptions<EnrichmentOptions> options) : IEnrichmentContextFactory
{
    private readonly EnrichmentOptions _options = options.Value;

    public async Task<EnrichmentContext> CreateAsync(
        Title title,
        Platform platform,
        CancellationToken cancellationToken = default)
    {
        var evidence = await evidenceReader.ReadAsync(title.Id, cancellationToken);

        var representative = SelectRepresentativeEvidence(evidence);

        var knownHashes = CollectOwnedHashes(evidence);

        // ExistingExternalId is set per-provider in the orchestrator loop
        return new EnrichmentContext
        {
            TitleName = title.Name,
            PlatformShortName = platform.ShortName,
            ExistingExternalId = null,
            Year = representative?.Year,
            Manufacturer = representative?.Manufacturer,
            Region = representative?.Region,
            KnownHashes = knownHashes
        };
    }

    /// <summary>
    ///     Selects the best representative game for metadata matching.
    ///     Scoring: verified ROM +1000, region priority (from config), no revision +50,
    ///     no dev status +25, has ROMs +10, tiebreak alphabetical.
    /// </summary>
    private TitleEnrichmentEvidence? SelectRepresentativeEvidence(IReadOnlyList<TitleEnrichmentEvidence> evidence)
    {
        if (evidence.Count == 0)
        {
            return null;
        }

        if (evidence.Count == 1)
        {
            return evidence[0];
        }

        return evidence
            .OrderByDescending(candidate => ScoreEvidence(candidate))
            .ThenBy(candidate => candidate.Name)
            .First();
    }

    private int ScoreEvidence(TitleEnrichmentEvidence evidence)
    {
        int score = 0;

        // TODO: Replace with structured verification evidence when name parsing populates it
        // Verified dump [!] from No-Intro naming — best for metadata matching (+1000)
        if (evidence.Name.Contains("[!]"))
        {
            score += 1000;
        }

        // User owns a ROM file for this game entry (+500)
        if (evidence.HasOwnedRom)
        {
            score += 500;
        }

        // Region priority from config (highest priority = highest score)
        string? region = evidence.Region?.ToUpperInvariant();
        if (region != null)
        {
            int regionIndex = _options.RegionPriority
                .FindIndex(r => r.Equals(region, StringComparison.OrdinalIgnoreCase));
            if (regionIndex >= 0)
            {
                score += 100 - (regionIndex * 10);
            }
        }

        // No revision means base release (+50)
        if (string.IsNullOrEmpty(evidence.Revision))
        {
            score += 50;
        }

        // No dev status means final release (+25)
        if (string.IsNullOrEmpty(evidence.DevelopmentStatus))
        {
            score += 25;
        }

        // Has ROMs defined (+10)
        if (evidence.HasDefinedRoms)
        {
            score += 10;
        }

        return score;
    }

    private static List<RomHashSet> CollectOwnedHashes(IReadOnlyList<TitleEnrichmentEvidence> evidence) =>
        evidence
            .SelectMany(candidate => candidate.OwnedRomHashes)
            .Select(hashes => new RomHashSet
            {
                Sha1 = hashes.Sha1,
                Md5 = hashes.Md5,
                Crc32 = hashes.Crc32,
                Size = hashes.Size
            })
            .ToList();
}
