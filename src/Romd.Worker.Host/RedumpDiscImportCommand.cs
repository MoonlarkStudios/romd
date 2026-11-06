using System.Globalization;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.ReferenceData;
using Romd.Persistence;
using Serilog;

namespace Romd.Worker.Host;

/// <summary>Imports a canonical reference without starting any worker hosted services.</summary>
internal static class RedumpDiscImportCommand
{
    internal const string Flag = "--import-redump-disc";
    internal const int Success = 0;
    internal const int Refused = 1;
    internal const int InvalidArguments = 2;
    internal const int Cancelled = 130;
    private static readonly TimeSpan _commandTimeout = TimeSpan.FromMinutes(2);

    internal static bool IsRequested(string[] args) =>
        args.Any(argument => argument.StartsWith(Flag, StringComparison.OrdinalIgnoreCase));

    internal static bool TryParse(string[] args, out int discId)
    {
        discId = 0;
        if (args.Length != 2 || args[0] != Flag)
            return false;
        string value = args[1];
        if (value.Length is < 1 or > 10 ||
            value.Any(character => character is < '0' or > '9'))
            return false;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out discId) && discId > 0;
    }

    internal static Task<int> RunAsync(string[] args) =>
        RunAsync(args, Program.CreateDiscReferenceImportHostBuilder, Console.Out, Console.Error);

    // Internal seams isolate command orchestration tests. Production always uses the closed
    // HTTPS composition below; neither a transport nor an arbitrary source is accepted by CLI.
    internal static async Task<int> RunAsync(string[] args, Func<IHostBuilder> hostBuilder,
        TextWriter output, TextWriter errors)
    {
        if (!TryParse(args, out int discId))
        {
            await errors.WriteLineAsync($"Usage: {Flag} <positive-disc-id>");
            return InvalidArguments;
        }

        using var budget = new CancellationTokenSource(_commandTimeout);
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            budget.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            using var host = hostBuilder().Build();
            // Build resolves normal configuration; StartAsync/RunAsync would start worker services
            // and is deliberately absent. All collaborators share this one scoped runtime context.
            await using var scope = host.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var imports = services.GetRequiredService<IRomDiscMediaReferenceImportStore>();
            bool requireWorkerRole = services.GetRequiredService<IHostEnvironment>().IsProduction();
            return await RunCoreAsync(discId, services.GetRequiredService<IRomdSchemaProbe>(),
                services.GetRequiredService<IUnitOfWork>(), requireWorkerRole,
                imports.GetRuntimeRoleAsync,
                () => new ImportOperation(RedumpDiscReferenceImportComposition.Create(
                    imports)),
                output, errors, budget.Token);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            await errors.WriteLineAsync("Reference import cancelled or exceeded its two-minute deadline.");
            return Cancelled;
        }
        catch (Exception failure)
        {
            // Never include connection strings, credentials, raw bodies or exception messages.
            Log.Error("Reference import failed ({FailureType})", failure.GetType().Name);
            await errors.WriteLineAsync($"Reference import failed ({failure.GetType().Name}).");
            return Refused;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    internal static async Task<int> RunCoreAsync(int discId, IRomdSchemaProbe schema, IUnitOfWork unitOfWork,
        bool requireWorkerRole, Func<CancellationToken, Task<string>> runtimeRole,
        Func<IImportOperation> createImport, TextWriter output, TextWriter errors, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string role = await runtimeRole(ct);
        ct.ThrowIfCancellationRequested();
        if (requireWorkerRole && role != "romd_worker")
        {
            await errors.WriteLineAsync("Reference import requires the romd_worker runtime database role in Production.");
            return Refused;
        }

        int? version = await schema.GetPublishedVersionAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (version != PostgreSqlConfiguration.ExpectedSchemaVersion)
        {
            await errors.WriteLineAsync("Reference import requires the worker-published application schema version.");
            return Refused;
        }

        using var importer = createImport();
        var prepared = await importer.PrepareAsync(discId, ct);
        ct.ThrowIfCancellationRequested();
        if (prepared.IsError)
            return await RefuseAsync(prepared.Errors, errors);

        // The exact prepared envelope stays in importer custody. No HTTP occurs under this
        // caller-owned transaction; errors, cancellation and exceptions dispose it uncommitted.
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        ct.ThrowIfCancellationRequested();
        var staged = await importer.StageAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (staged.IsError)
            return await RefuseAsync(staged.Errors, errors);
        if (staged.Value != prepared.Value)
            return await RefuseAsync([RomDiscMediaReferenceImportErrors.InvalidStagedResult()], errors);

        await transaction.CommitAsync(ct);
        var record = staged.Value;
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            recordId = record.Id,
            rawSha256 = record.RawReferenceSha256.ToString(),
            rawBytes = record.RawReferenceBytes,
            retrievedAtUtc = record.RetrievedAt.ToString("O", CultureInfo.InvariantCulture),
            source = record.ReferenceSource,
            system = record.System.ToString(),
            media = record.Media.ToString(),
            fileCount = record.FileCount,
            logicalBytes = record.LogicalBytes,
            sha1 = record.Sha1.ToString(),
            md5 = record.Md5.ToString(),
            crc32 = record.Crc32.ToString()
        }));
        return Success;
    }

    private static async Task<int> RefuseAsync(List<Error> failures, TextWriter errors)
    {
        foreach (var failure in failures)
            await errors.WriteLineAsync($"{failure.Code}: {failure.Description}");
        return Refused;
    }

    internal interface IImportOperation : IDisposable
    {
        Task<ErrorOr<RomDiscMediaReferenceRecord>> PrepareAsync(int discId, CancellationToken ct);
        Task<ErrorOr<RomDiscMediaReferenceRecord>> StageAsync(CancellationToken ct);
    }

    private sealed class ImportOperation : IImportOperation
    {
        private readonly RedumpDiscReferenceImporter _importer;
        private RedumpDiscReferenceImport? _prepared;

        public ImportOperation(RedumpDiscReferenceImporter importer) => _importer = importer;

        public async Task<ErrorOr<RomDiscMediaReferenceRecord>> PrepareAsync(int discId, CancellationToken ct)
        {
            if (_prepared is not null)
                throw new InvalidOperationException("A retrieval has already been prepared.");
            var prepared = await _importer.PrepareAsync(discId, ct: ct);
            if (prepared.IsError)
                return prepared.Errors;
            _prepared = prepared.Value;
            return ErrorOrFactory.From(_prepared.Record);
        }

        public Task<ErrorOr<RomDiscMediaReferenceRecord>> StageAsync(CancellationToken ct) =>
            _importer.StageAsync(_prepared ?? throw new InvalidOperationException("No retrieval has been prepared."), ct);

        public void Dispose() => _importer.Dispose();
    }
}
