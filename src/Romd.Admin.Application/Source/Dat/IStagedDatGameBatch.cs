namespace Romd.Admin.Application.Source.Dat;

/// <summary>
///     This batch's generated game identities in input order. The caller flushes the new graph
///     before reading and owns the transaction; the receipt cannot outlive that boundary.
/// </summary>
public interface IStagedDatGameBatch
{
    IReadOnlyList<int> ReadIdsAfterFlush();
}
