using System.Runtime.ExceptionServices;

namespace Romd.Hosting.IntegrationTests.Vfs;

internal static class VfsProbeEvidence
{
    internal const string FailureTypesKey = "Romd.VfsProbeEvidence.FailureTypes";

    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> action,
        Func<CancellationToken, Task> writeEvidence,
        Func<CancellationToken, Task> cleanup,
        Action<string> reportFailureType,
        CancellationToken ct)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var failureTypes = new List<string>();

        void Report(string phase, Exception error)
        {
            string diagnostic = $"{phase}:{error.GetType().FullName}";
            failureTypes.Add(diagnostic);
            try
            { reportFailureType(diagnostic); }
            catch (Exception reportError)
            {
                // A failing diagnostic sink must not prevent cleanup or replace
                // the primary error. Its safe type remains attached to that error.
                failureTypes.Add($"diagnostic:{reportError.GetType().FullName}");
            }
        }

        async Task FinalizeAsync(Func<CancellationToken, Task> operation, string phase)
        {
            try
            { await operation(CancellationToken.None); }
            catch (Exception error)
            {
                failure ??= ExceptionDispatchInfo.Capture(error);
                Report(phase, error);
            }
        }

        try
        { result = await action(ct); }
        catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        await FinalizeAsync(writeEvidence, "evidence");
        await FinalizeAsync(cleanup, "cleanup");
        if (failure is not null)
        {
            if (failureTypes.Count > 0)
            {
                try
                { failure.SourceException.Data[FailureTypesKey] = failureTypes.ToArray(); }
                catch (Exception)
                {
                    // Custom Exception.Data implementations can reject mutation;
                    // reporting has already been attempted without exposing data.
                }
            }
            failure.Throw();
        }
        return result;
    }
}
