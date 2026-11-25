namespace Romd.Contracts.Management.Models;

public sealed record ServerImportRetryFileGroup(int Count, IReadOnlyList<string> Examples);

/// <summary>An observation of the originals selected by one retained server-folder import.</summary>
public sealed record ServerImportRetryReview(
    string Revision, bool IsBusy, ServerImportRetryFileGroup Ready, ServerImportRetryFileGroup Changed,
    ServerImportRetryFileGroup Missing, ServerImportRetryFileGroup Unavailable);
