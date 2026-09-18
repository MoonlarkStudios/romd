using Microsoft.Extensions.Logging;
using Romd.Application.Common.Configuration;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs;

public sealed class UploadJobCreator : IUploadJobCreator
{
    private readonly IUploadJobRepository _jobRepo;
    private readonly IJobRepository _allJobs;
    private readonly ILogger<UploadJobCreator> _logger;
    private readonly IRomdOptions _options;

    public UploadJobCreator(
        IUploadJobRepository jobRepo,
        IJobRepository allJobs,
        IRomdOptions options,
        ILogger<UploadJobCreator> logger)
    {
        _jobRepo = jobRepo;
        _allJobs = allJobs;
        _options = options;
        _logger = logger;
    }

    public async Task<UploadJobCreationResult> CreateAsync(
        Stream fileStream,
        string fileName,
        UploadJobOptions options,
        CancellationToken ct = default)
    {
        fileName = Path.GetFileName(fileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..")
            throw new ArgumentException("A valid filename is required.", nameof(fileName));

        var job = UploadJob.Create(fileName, options.PlatformId, options.CreatedByUserId,
            options.RequestId, options.BatchId);
        UploadJobCreationResult Reuse(Job previous)
        {
            if (previous is not UploadJob existing
                || existing.CreatedByUserId != options.CreatedByUserId
                || existing.SourceFilename != fileName
                || existing.CorrelationId != job.CorrelationId
                || existing.PlatformId != options.PlatformId
                || existing.AllowUnidentified != options.AllowUnidentified
                || existing.ArchiveOnly != options.ArchiveOnly
                || existing.MaxParallelRoms != options.MaxParallelRoms)
                throw new InvalidOperationException("This upload request ID has already been used for another import.");
            return new UploadJobCreationResult(existing.Id, existing.Id.ToString("N"), $"/jobs/{existing.Id}");
        }
        var accepted = await _allJobs.GetByIdAsync(job.Id, ct);
        if (accepted is not null)
            return Reuse(accepted);
        // Share the worker's cross-process lock: an accepted job's workspace must never be
        // overwritten by a repeated POST, even while extraction removes its original archive.
        await using var acceptanceLock = await JobWorkspace.AcquireExecutionLockAsync(_options.DataDirectory, job.Id, ct);
        accepted = await _allJobs.GetByIdAsync(job.Id, ct);
        if (accepted is not null)
            return Reuse(accepted);
        job.SetMaxParallelRoms(options.MaxParallelRoms);
        job.SetAllowUnidentified(options.AllowUnidentified);
        job.SetArchiveOnly(options.ArchiveOnly);

        // Create work directory
        string workDirectory = Path.Combine(
            _options.DataDirectory,
            "temp",
            "jobs",
            job.Id.ToString("N"));

        // An unaccepted interrupted transfer may have left partial bytes. No worker can own
        // this directory: the authoritative lookup above found no job while holding its lock.
        if (Directory.Exists(workDirectory))
            Directory.Delete(workDirectory, recursive: true);
        Directory.CreateDirectory(workDirectory);

        // Persist file to work directory
        string filePath = Path.Combine(workDirectory, fileName);
        try
        {
            long available = new DriveInfo(workDirectory).AvailableFreeSpace;
            if (fileStream.CanSeek && fileStream.Length > available - (1L << 30))
                throw new InvalidOperationException("Insufficient free space to stage this upload.");
            await using var target = File.Create(filePath);
            await fileStream.CopyToAsync(target, ct);
        }
        catch
        {
            Directory.Delete(workDirectory, recursive: true);
            throw;
        }

        // Persist job to database
        await _jobRepo.AddAsync(job, ct);

        _logger.LogInformation(
            "Created upload job {JobId} for {FileName}",
            job.Id, fileName);

        // Acceptance includes the durable dispatch intent; transport availability is independent.
        return new UploadJobCreationResult(job.Id, job.Id.ToString("N"), $"/jobs/{job.Id}");
    }
}
