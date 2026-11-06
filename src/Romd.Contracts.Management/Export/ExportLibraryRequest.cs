namespace Romd.Contracts.Management.Export;

public sealed record ExportLibraryRequest
{
    public string? LibraryId { get; init; }
    public string? Format { get; init; }
}
