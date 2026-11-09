using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Queries.ExportJobItems;
using Romd.Application.Common.Systems;
using Romd.Contracts.Management.Models;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Romd.Host.Endpoints;

/// <summary>Streams the complete resolved ledger while retaining its repeatable read snapshot.</summary>
public sealed class JobItemExportResult(Guid jobId, string format, JobItemExport export, SystemKeys systemKeys) : IResult
{
    internal const string CsvHeader = "filename,kind,outcome,sizeBytes,platform,matchedTitles,gameCount,archiveOnly,error";

    public async Task ExecuteAsync(HttpContext context)
    {
        var logger = context.RequestServices.GetService<ILogger<JobItemExportResult>>() ?? NullLogger<JobItemExportResult>.Instance;
        try
        {
            IAsyncEnumerator<JobItemView>? items = null;
            bool completed = false;
            Exception? primary = null;
            try
            {
                var ct = context.RequestAborted;
                items = export.ReadAsync(ct).GetAsyncEnumerator(ct);
                // Resolve and map the first item before file headers or bytes are written, so a
                // preflight failure can still use the normal sanitized API problem response.
                bool hasItem = await items.MoveNextAsync();
                var first = hasItem ? items.Current.ToContract(systemKeys) : null;
                ct.ThrowIfCancellationRequested();
                context.Response.ContentType = format == "json" ? "application/json" : "text/csv; charset=utf-8";
                context.Response.Headers.ContentDisposition = $"attachment; filename=import-{jobId:N}.{format}";

                if (format == "json")
                    await WriteJsonAsync(context, items, first, hasItem, logger, ct);
                else
                    await WriteCsvAsync(context, items, first, hasItem, logger, ct);

                // A read-only transaction is committed only after the complete body was accepted by
                // the response writer. Any interrupted read/write leaves disposal to roll it back.
                await export.CompleteAsync(ct);
                completed = true;
            }
            catch (Exception error) { primary = error; throw; }
            finally
            {
                await ReadSnapshotCleanup.DisposeAsync(items is null ? [export] : [items, export], completed, primary, logger);
            }
        }
        catch
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
            {
                context.Response.ContentType = null;
                context.Response.Headers.Remove("Content-Disposition");
            }
            throw;
        }
    }

    private async Task WriteJsonAsync(HttpContext context, IAsyncEnumerator<JobItemView> items, JobItem? first,
        bool hasItem, ILogger logger, CancellationToken ct)
    {
        var options = context.RequestServices.GetService<IOptions<JsonOptions>>()?.Value.SerializerOptions
            ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var writer = new Utf8JsonWriter(context.Response.BodyWriter);
        Exception? primary = null;
        try
        {
            writer.WriteStartArray();
            if (hasItem)
                JsonSerializer.Serialize(writer, first, options);
            await FlushJsonAsync(writer, context.Response.BodyWriter, ct);
            long flushedBytes = writer.BytesCommitted;
            while (await items.MoveNextAsync())
            {
                JsonSerializer.Serialize(writer, items.Current.ToContract(systemKeys), options);
                // Growing the IBufferWriter can advance segments without a transport flush.
                // Count both advanced and pending bytes to retain bounded backpressure.
                if (writer.BytesCommitted + writer.BytesPending - flushedBytes >= 16 * 1024)
                {
                    await FlushJsonAsync(writer, context.Response.BodyWriter, ct);
                    flushedBytes = writer.BytesCommitted;
                }
            }
            writer.WriteEndArray();
            await FlushJsonAsync(writer, context.Response.BodyWriter, ct);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await ReadSnapshotCleanup.DisposeAsync([writer], false, primary, logger); }
    }

    private async Task WriteCsvAsync(HttpContext context, IAsyncEnumerator<JobItemView> items, JobItem? first,
        bool hasItem, ILogger logger, CancellationToken ct)
    {
        var writer = new StreamWriter(context.Response.Body, new UTF8Encoding(false), leaveOpen: true);
        Exception? primary = null;
        try
        {
            await writer.WriteAsync((CsvHeader + "\n").AsMemory(), ct);
            if (hasItem)
                await writer.WriteAsync(CsvRow(first!).AsMemory(), ct);
            await writer.FlushAsync(ct);
            while (await items.MoveNextAsync())
                await writer.WriteAsync(CsvRow(items.Current.ToContract(systemKeys)).AsMemory(), ct);
            await writer.FlushAsync(ct);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await ReadSnapshotCleanup.DisposeAsync([writer], false, primary, logger); }
    }

    private static async Task FlushJsonAsync(Utf8JsonWriter writer, PipeWriter transport, CancellationToken ct)
    {
        await writer.FlushAsync(ct);
        // Utf8JsonWriter only advances an IBufferWriter; flushing the PipeWriter is the actual
        // network/backpressure boundary and may signal a disconnected or canceled reader.
        var result = await transport.FlushAsync(ct);
        if (result.IsCanceled)
            throw new OperationCanceledException("The export response was canceled.", ct);
        if (result.IsCompleted)
            throw new IOException("The export response stopped accepting data.");
    }

    internal static string CsvRow(JobItem item) => string.Join(',',
        Csv(item.FileName), Csv(item.Kind), Csv(item.Outcome), item.SizeBytes.ToString(),
        Csv(item.PlatformName ?? string.Empty), Csv(string.Join("; ", item.MatchedTitles.Select(title => title.Name))),
        item.GameCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        item.ArchiveOnly?.ToString().ToLowerInvariant() ?? string.Empty, Csv(item.Error ?? string.Empty)) + "\n";

    private static string Csv(string value) => value.IndexOfAny(['"', ',', '\n', '\r']) >= 0
        ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
        : value;
}
