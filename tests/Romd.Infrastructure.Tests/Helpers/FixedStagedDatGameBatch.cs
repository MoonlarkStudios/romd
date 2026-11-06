using Romd.Admin.Application.Source.Dat;

namespace Romd.Infrastructure.Tests.Helpers;

internal sealed class FixedStagedDatGameBatch(IEnumerable<int> ids) : IStagedDatGameBatch
{
    private readonly int[] _ids = ids.ToArray();
    public IReadOnlyList<int> ReadIdsAfterFlush() => _ids.ToArray();
}
