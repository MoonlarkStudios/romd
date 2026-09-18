using Romd.Admin.Application.Diagnostics;
using Romd.Application.Common.Jobs;

namespace Romd.Infrastructure.Diagnostics;

public sealed class HangfireDiagnosticsReader(IHangfireDiagnosticsSource source) : IHangfireDiagnosticsReader
{
    private static readonly string[] KnownQueues =
    [
        JobQueue.Default.Value,
        JobQueue.Upload.Value,
        JobQueue.Enrichment.Value,
        JobQueue.Materialization.Value
    ];

    public async Task<HangfireDiagnosticsData> ReadAsync(
        int serverLimit,
        int recurringJobLimit,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(serverLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(recurringJobLimit, 1);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        try
        {
            if (!source.IsAvailable)
            {
                return Unavailable("Hangfire storage is not registered in this host.");
            }

            ct.ThrowIfCancellationRequested();
            var snapshot = source.Read(
                serverLimit + 1,
                recurringJobLimit + 1,
                KnownQueues,
                ct);

            return new HangfireDiagnosticsData(
                true,
                null,
                new BoundedDiagnosticsData<HangfireServerDiagnosticsData>(
                    snapshot.Servers.Take(serverLimit).Select(Sanitize).ToList(),
                    snapshot.Servers.Count > serverLimit),
                new BoundedDiagnosticsData<RecurringJobDiagnosticsData>(
                    snapshot.RecurringJobs.Take(recurringJobLimit).Select(Sanitize).ToList(),
                    snapshot.RecurringJobs.Count > recurringJobLimit),
                snapshot.QueueBacklogs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Unavailable(ClampError(ex.Message));
        }
    }

    private static HangfireDiagnosticsData Unavailable(string error) => new(
        false,
        error,
        new BoundedDiagnosticsData<HangfireServerDiagnosticsData>([], false),
        new BoundedDiagnosticsData<RecurringJobDiagnosticsData>([], false),
        []);

    private static string ClampError(string error) =>
        error[..Math.Min(error.Length, OperationalDiagnosticsPolicy.ErrorTextLimit)];

    private static string ClampText(string text) =>
        text[..Math.Min(text.Length, OperationalDiagnosticsPolicy.ProviderTextLimit)];

    private static string? ClampOptionalText(string? text) => text is null ? null : ClampText(text);

    private static HangfireServerDiagnosticsData Sanitize(HangfireServerDiagnosticsData server) => server with
    {
        Name = ClampText(server.Name),
        Queues = server.Queues
            .Take(OperationalDiagnosticsPolicy.HangfireServerQueueLimit)
            .Select(ClampText)
            .ToList()
    };

    private static RecurringJobDiagnosticsData Sanitize(RecurringJobDiagnosticsData job) => job with
    {
        Id = ClampText(job.Id),
        Queue = ClampText(job.Queue),
        Cron = ClampText(job.Cron),
        LastJobState = ClampOptionalText(job.LastJobState),
        Error = job.Error is null ? null : ClampError(job.Error)
    };
}
