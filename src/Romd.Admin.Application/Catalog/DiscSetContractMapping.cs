using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Libraries;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;

using ContractDiscSetDefinition = Romd.Contracts.Management.Models.DiscSetDefinition;
using ContractDiscSetMember = Romd.Contracts.Management.Models.DiscSetMember;

using DiscSetDefinition = Romd.Domain.Catalog.DiscSetDefinition;

namespace Romd.Admin.Application.Catalog;

/// <summary>Pure projections of owned disc-set facts into the existing wire contract.</summary>
public static class DiscSetContractMapping
{
    public static DiscSet ToDto(DiscSetSnapshot row, DiscSetClaims claims)
    {
        ContractDiscSetDefinition Map(DiscSetDefinition value) => new(value.Name, value.ExpectedDiscCount,
            Resolve(value, row.Facts.SourceId, claims), value.ReviewReasons, value.RuleVersion);
        return new(IdCoder.Encode(row.Id), IdCoder.Encode(row.Facts.SourceId), row.SystemKey, row.Revision,
            row.Facts.Decision, Map(row.Facts.Proposal), row.Facts.Correction is null ? null : Map(row.Facts.Correction),
            row.DecidedBy, row.DecidedAt, row.Reason);
    }
    public static IReadOnlyList<ContractDiscSetMember> Resolve(DiscSetDefinition definition, int sourceId, DiscSetClaims claims) =>
        DiscSetDecisionPolicy.Resolve(definition, sourceId, claims).Select(member => new ContractDiscSetMember(
            member.EntryKey, member.Name, member.Ordinal, member.ReleaseId is int id ? IdCoder.Encode(id) : null, member.Issue)).ToArray();

    public static LibraryDiscSetOption ToOption(DiscSetSnapshot row, DiscSetClaims claims, IReadOnlySet<int> eligibleReleaseIds)
    {
        var definition = row.Facts.Effective;
        var eligibility = DiscSetDecisionPolicy.Eligibility(row.Facts, claims, eligibleReleaseIds);
        return new(IdCoder.Encode(row.Id), definition.Name, definition.Members.Select(member => member.TitleId).Distinct().Count() == 1
            ? IdCoder.Encode(definition.Members[0].TitleId) : null, row.SystemKey, definition.Members.Count,
            definition.ExpectedDiscCount, eligibility.IsEligible,
            !eligibility.IsValid ? "UnresolvedGrouping" : !eligibility.IsEligible ? "OutsideEditableLibraryPolicy" : null);
    }
}
