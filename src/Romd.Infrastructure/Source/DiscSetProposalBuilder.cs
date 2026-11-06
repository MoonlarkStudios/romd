using System.Security.Cryptography;
using System.Text;
using Romd.Dat.Parsing;
using Romd.Dat.Parsing.Models;
using Romd.Domain.Catalog;

namespace Romd.Infrastructure.Source;

/// <summary>Pure DAT-name derivation. Catalog transactions and persistence own reads and updates.</summary>
public static class DiscSetProposalBuilder
{
    public static IReadOnlyList<DiscSetProposal> Create(IReadOnlyList<DiscSetSourceClaim> claims)
    {
        var desired = new List<DiscSetProposal>();
        foreach (var scope in claims.GroupBy(x => x.SourceId))
        {
            // Parse once per source; partition only on the parser's exact preserved edition key.
            var parsed = scope.Select(c => (Claim: c, Parsed: DiscNameParser.Parse(c.Name))).ToArray();
            foreach (var group in parsed.Where(x => x.Parsed.Status == DiscNameStatus.Parsed).GroupBy(x => x.Parsed.GroupingKey!))
            {
                var preview = DiscGroupingPreview.Create(scope.Key.ToString(), group.Select(x => x.Claim.Name)).Groups.Single();
                var reasons = preview.ReviewReasons.Select(x => x.ToString()).ToList();
                string baseName = preview.EditionName.Split('(')[0].Trim();
                if (parsed.Any(x => x.Parsed.Status == DiscNameStatus.NeedsReview && x.Claim.Name.Split('(')[0].Trim().Equals(baseName, StringComparison.OrdinalIgnoreCase)))
                    reasons.Add("UnresolvedDiscRoleSibling");
                if (group.Select(x => x.Claim.TitleId).Distinct().Count() != 1 || group.Any(x => x.Claim.Conflict))
                    reasons.Add("TitleConflict");
                if (group.Select(x => x.Claim.Fingerprint).Distinct().Count() != group.Count())
                    reasons.Add("RepeatedRelease");
                var members = group.OrderBy(x => x.Parsed.DiscNumber).ThenBy(x => x.Claim.EntryKey, StringComparer.Ordinal)
                    .Select(x => new DiscSetMember(x.Claim.EntryKey, x.Claim.Name, x.Claim.Fingerprint, x.Parsed.DiscNumber!.Value, x.Claim.TitleId)).ToArray();
                desired.Add(new(scope.Key, Hash("edition:" + group.Key), new(preview.EditionName, preview.DeclaredDiscCount,
                    members, reasons, DiscNameParser.RuleVersion)));
            }
            foreach (var item in parsed.Where(x => x.Parsed.Status == DiscNameStatus.NeedsReview))
                desired.Add(new(scope.Key, Hash("review:" + item.Claim.EntryKey), new(item.Claim.Name, null,
                    [new(item.Claim.EntryKey, item.Claim.Name, item.Claim.Fingerprint, 1, item.Claim.TitleId)],
                    [item.Parsed.Issue?.ToString() ?? "UnusualDiscRole"], DiscNameParser.RuleVersion)));
        }
        return desired;
    }
    private static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
