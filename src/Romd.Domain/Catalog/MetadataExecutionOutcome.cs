namespace Romd.Domain.Catalog;

/// <summary>
///     Whether a run item executed its metadata step. Succeeded means the step ran without an execution error,
///     not that metadata is complete or a match is trusted. Names are persisted text: never rename or reuse them.
///     Within one item a later attempt only raises the record: NotAttempted, then Failed, then Succeeded.
/// </summary>
public enum MetadataExecutionOutcome
{
    Succeeded,
    Failed,
    NotAttempted
}
