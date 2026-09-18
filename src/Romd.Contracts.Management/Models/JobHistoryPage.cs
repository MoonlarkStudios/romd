namespace Romd.Contracts.Management.Models;

public sealed record JobHistoryPage(IReadOnlyList<JobDto> Items, string? NextCursor);
