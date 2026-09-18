namespace Romd.Admin.Application.Ingestion.Jobs;

/// <summary>An executor's non-retryable failure. Message must be safe for persisted job errors and the UI.</summary>
public sealed class JobPermanentFailureException(string message) : Exception(message);
