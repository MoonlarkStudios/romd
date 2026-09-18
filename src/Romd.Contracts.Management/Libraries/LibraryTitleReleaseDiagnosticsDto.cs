namespace Romd.Contracts.Management.Libraries;

public sealed record LibraryTitleReleaseDiagnosticsDto
{
    public required string Id { get; init; }
    public required string DatId { get; init; }
    public required string Name { get; init; }
    public required bool IsEligible { get; init; }
    public required bool IsBlocked { get; init; }
    public string? BlockReason { get; init; }
    public required bool IsExposed { get; init; }
    public string? ExposureReason { get; init; }
}
