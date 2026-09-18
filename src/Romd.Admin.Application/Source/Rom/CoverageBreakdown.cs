namespace Romd.Admin.Application.Source.Rom;

public sealed record CoverageBreakdown(
    int ExpectedTitleCount,
    int LocalPayloadTitleCount,
    int CompleteTitleCount,
    int PartialTitleCount);
