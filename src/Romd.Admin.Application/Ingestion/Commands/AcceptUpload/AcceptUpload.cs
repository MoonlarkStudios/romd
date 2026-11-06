using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Classification;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Commands.AcceptUpload;

public enum UploadKind { Generic, Dat, Rom }

public sealed record AcceptUploadCommand(
    UploadKind Kind, IUploadInput? Input, int? PlatformId = null,
    int? MaxParallelRoms = null, bool? AllowUnidentified = null, bool? ArchiveOnly = null,
    bool? TrackedOnly = null, Guid? RequestId = null, Guid? BatchId = null) : ICommand<Guid>;

public sealed class AcceptUploadCommandHandler(
    ITrackedTitleReader trackedTitles, IFileClassifier classifier, IJobRepository jobs,
    IJobWorkspaceExclusion exclusion, IUploadWorkspacePreparation workspace,
    IUploadJobAcceptance acceptance, IUnitOfWork work, ICurrentUser currentUser,
    IRomdOptions options, TimeProvider clock, ILogger<AcceptUploadCommandHandler> logger)
    : ICommandHandler<AcceptUploadCommand, Guid>
{
    public async Task<ErrorOr<Guid>> HandleAsync(AcceptUploadCommand command, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(command.Kind))
            throw new ArgumentOutOfRangeException(nameof(command));
        var input = command.Input;
        if (input is null || input.Length == 0 || string.IsNullOrWhiteSpace(input.FileName)
            || Path.GetFileName(input.FileName.Replace('\\', '/')) is "" or "." or "..")
            return UploadErrors.FileRequired();
        if (command.Kind != UploadKind.Dat)
        {
            long maximum = options.MaxUploadBytes > 0 ? options.MaxUploadBytes : 64L * 1024 * 1024 * 1024;
            if (input.Length > maximum)
                return UploadErrors.FileTooLarge(maximum);
        }
        if (command.Kind == UploadKind.Generic)
        {
            if (command.MaxParallelRoms is < 1 or > 32)
                return UploadErrors.InvalidConcurrency();
            if (command.RequestId == Guid.Empty || command.BatchId == Guid.Empty
                || command.RequestId.HasValue != command.BatchId.HasValue)
                return UploadErrors.InvalidIdentity();
        }
        if (command.Kind != UploadKind.Dat && command.TrackedOnly == true
            && !await trackedTitles.HasTrackedAsync(cancellationToken: ct))
            return UploadErrors.NoTrackedTitles();

        Stream? classification = null;
        Stream? content = null;
        IJobWorkspaceLease? lease = null;
        ITransaction? transaction = null;
        UploadJob? job = null;
        bool committed = false;
        bool durableAcceptance = false;
        Exception? primary = null;
        try
        {
            if (command.Kind != UploadKind.Generic)
            {
                classification = input.OpenRead();
                var result = await classifier.ClassifyAsync(classification, input.FileName, ct);
                if (command.Kind == UploadKind.Dat && result.Type != FileType.Dat)
                    return UploadErrors.InvalidDatFile(result.Type);
                if (command.Kind == UploadKind.Rom && result.Type == FileType.Dat)
                    return UploadErrors.InvalidRomFile();
            }
            content = input.OpenRead();
            var actor = currentUser.UserId;
            string filename = Path.GetFileName(input.FileName.Replace('\\', '/'));
            // The clock and fresh identity are sampled even for replay, before either lookup.
            job = UploadJob.Create(filename, clock.GetUtcNow(),
                command.Kind == UploadKind.Rom ? null : command.PlatformId, actor,
                command.Kind == UploadKind.Generic ? command.RequestId : null,
                command.Kind == UploadKind.Generic ? command.BatchId : null);
            var settings = command.Kind == UploadKind.Generic
                ? new UploadJobOptions
                {
                    PlatformId = command.PlatformId,
                    CreatedByUserId = actor,
                    MaxParallelRoms = command.MaxParallelRoms ?? 4,
                    AllowUnidentified = command.AllowUnidentified ?? false,
                    ArchiveOnly = command.ArchiveOnly ?? false,
                    TrackedOnly = command.TrackedOnly ?? false
                }
                : new UploadJobOptions
                {
                    PlatformId = job.PlatformId,
                    CreatedByUserId = actor,
                    TrackedOnly = command.Kind == UploadKind.Rom && command.TrackedOnly == true
                };
            var previous = await jobs.GetByIdAsync(job.Id, ct);
            if (previous is not null)
            {
                var replay = Reuse(previous, job, settings);
                durableAcceptance = !replay.IsError;
                return replay;
            }
            lease = await exclusion.AcquireAsync(job.Id, ct);
            previous = await jobs.GetByIdAsync(job.Id, ct);
            if (previous is not null)
            {
                var replay = Reuse(previous, job, settings);
                durableAcceptance = !replay.IsError;
                return replay;
            }
            job.SetMaxParallelRoms(settings.MaxParallelRoms);
            job.SetAllowUnidentified(settings.AllowUnidentified);
            job.SetArchiveOnly(settings.ArchiveOnly);
            job.SetTrackedOnly(settings.TrackedOnly);
            var prepared = await workspace.PrepareAsync(new(job.Id, filename), content, lease, ct);
            if (prepared.IsError)
                return prepared.Errors;
            transaction = await work.BeginTransactionAsync(ct);
            await acceptance.AddStagedAsync(job, ct);
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(ct);
            committed = true;
            durableAcceptance = true;
            return job.Id;
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            // Complete inputs survive every acceptance fault, including a lost commit acknowledgement.
            // Keep the worker/orphan exclusion through transaction and owned-stream disposal.
            Exception? cleanup = null;
            await ReleaseAsync(transaction, "transaction");
            await ReleaseAsync(content, "upload stream");
            await ReleaseAsync(classification, "classification stream");
            await ReleaseAsync(lease, "workspace lease");
            if (primary is null && !durableAcceptance && cleanup is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanup).Throw();

            async ValueTask ReleaseAsync(IAsyncDisposable? resource, string name)
            {
                if (resource is null)
                    return;
                try
                { await resource.DisposeAsync(); }
                catch (Exception error)
                {
                    cleanup ??= error;
                    logger.LogWarning(error, "Failed to release {Resource} for upload job {JobId}; commit acknowledged: {Committed}",
                        name, job?.Id, committed);
                }
            }
        }
    }

    private static ErrorOr<Guid> Reuse(Job previous, UploadJob job, UploadJobOptions settings) =>
        previous is UploadJob existing
        && existing.CreatedByUserId == settings.CreatedByUserId
        && existing.SourceFilename == job.SourceFilename
        && existing.CorrelationId == job.CorrelationId
        && existing.PlatformId == settings.PlatformId
        && existing.AllowUnidentified == settings.AllowUnidentified
        && existing.ArchiveOnly == settings.ArchiveOnly
        && existing.TrackedOnly == settings.TrackedOnly
        && existing.MaxParallelRoms == settings.MaxParallelRoms
            ? existing.Id : UploadErrors.RequestIdReused();
}
