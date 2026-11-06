using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Application.Common.ReferenceCatalog;

namespace Romd.Application.Common.Systems;

public sealed record SystemSummaryData(string Key, string Name, string CompactLabel, ReferenceAssetData? Icon = null);

public static class SystemSummaryMapping
{
    public static SystemSummaryDto ToContract(this SystemSummaryData system) => new(
        system.Key, system.Name, system.CompactLabel,
        system.Icon?.ToContract());
}
