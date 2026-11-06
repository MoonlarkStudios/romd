using Romd.Application.Common.Content;

namespace Romd.Admin.Application.Source.Rom.Reconciliation;

/// <summary>When reconciliation requests a first structural observation of a logical whole file.</summary>
public static class LogicalMediaInspectionPolicy
{
    // Preserve the existing admission boundary; tiny opaque inputs do not schedule a structural inspector.
    public const long MinimumContentBytes = 129;

    public static bool NeedsInspection(RomContentDescriptor content) =>
        content.Inspections.Count == 0 && content.ContentSize >= MinimumContentBytes;
}
