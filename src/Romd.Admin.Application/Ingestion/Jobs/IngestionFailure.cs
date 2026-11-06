using System.Data.Common;
using Romd.Application.Common.Content;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Jobs;

public enum IngestionFailureKind { Permanent, Capacity, Uncertain, OwnershipLost, Cancelled }

public static class IngestionFailure
{
    public static IngestionFailureKind Classify(Exception error) => error switch
    {
        OperationCanceledException => IngestionFailureKind.Cancelled,
        JobExecutionOwnershipLostException => IngestionFailureKind.OwnershipLost,
        UploadOutcomeUncertainException => IngestionFailureKind.Uncertain,
        ResourceCapacityException or TimeoutException => IngestionFailureKind.Capacity,
        InvalidDataException or PathTooLongException or FileNotFoundException or DirectoryNotFoundException => IngestionFailureKind.Permanent,
        IOException => IngestionFailureKind.Capacity,
        DbException db when db.IsTransient => IngestionFailureKind.Capacity,
        _ when error.InnerException is { } inner && Classify(inner) == IngestionFailureKind.Capacity => IngestionFailureKind.Capacity,
        _ => IngestionFailureKind.Permanent
    };

    public static bool RequiresDeferral(Exception error) => Classify(error) is IngestionFailureKind.Capacity or IngestionFailureKind.Uncertain;
}
