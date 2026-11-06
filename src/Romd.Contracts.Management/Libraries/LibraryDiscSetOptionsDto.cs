using Romd.Contracts.Common.Models;
namespace Romd.Contracts.Management.Libraries;

public sealed record LibraryDiscSetOptionDto(string Id, string Name, string? TitleId, string? SystemKey,
    int MemberCount, int? ExpectedDiscCount, bool IsEligible, string? Issue);
public sealed record LibraryDiscSetOptionsDto(IReadOnlyList<string> SelectedIds,
    IReadOnlyList<LibraryDiscSetOptionDto> Selected, Page<LibraryDiscSetOptionDto> Page);
