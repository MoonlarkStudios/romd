using Romd.Contracts.Common.Models;
namespace Romd.Contracts.Management.Libraries;

public sealed record LibraryDiscSetOption(string Id, string Name, string? TitleId, string? SystemKey,
    int MemberCount, int? ExpectedDiscCount, bool IsEligible, string? Issue);
public sealed record LibraryDiscSetOptions(IReadOnlyList<string> SelectedIds,
    IReadOnlyList<LibraryDiscSetOption> Selected, Page<LibraryDiscSetOption> Page);
