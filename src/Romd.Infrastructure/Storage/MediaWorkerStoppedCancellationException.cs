namespace Romd.Infrastructure.Storage;

/// <summary>Cancellation with affirmative proof that no child started or the complete group stopped.</summary>
internal sealed class MediaWorkerStoppedCancellationException(CancellationToken ct)
    : OperationCanceledException("The media operation was canceled after confirming child stop.", ct);
