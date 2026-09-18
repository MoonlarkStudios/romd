using Microsoft.Extensions.Logging;
using Romd.Application.Common.Configuration;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs;

/// <summary>
///     Creates and enqueues DAT replacement jobs.
/// </summary>
public sealed class ReplaceDatJobCreator : IReplaceDatJobCreator
{
    private readonly IJobRepository<ReplaceDatJob> _jobRepo;
    private readonly ILogger<ReplaceDatJobCreator> _logger;
    private readonly IRomdOptions _options;

    public ReplaceDatJobCreator(
        IJobRepository<ReplaceDatJob> jobRepo,
        IRomdOptions options,
        ILogger<ReplaceDatJobCreator> logger)
    {
        _jobRepo = jobRepo;
        _options = options;
        _logger = logger;
    }

    public async Task<ReplaceDatJobCreationResult> CreateAsync(
        int existingDatId,
        Stream fileStream,
        string fileName,
        int? platformId,
        CancellationToken ct = default)
    {
        var job = ReplaceDatJob.Create(existingDatId, fileName, platformId);

        // Create work directory
        string workDirectory = Path.Combine(
            _options.DataDirectory,
            "temp",
            "jobs",
            job.Id.ToString("N"));

        Directory.CreateDirectory(workDirectory);

        // Persist file to work directory
        string filePath = Path.Combine(workDirectory, fileName);
        await using (var target = File.Create(filePath))
        {
            await fileStream.CopyToAsync(target, ct);
        }

        // Persist job to database
        await _jobRepo.AddAsync(job, ct);

        _logger.LogInformation(
            "Created replace DAT job {JobId} for existing DAT {ExistingDatId} with file {FileName}",
            job.Id, existingDatId, fileName);

        // Acceptance includes the durable dispatch intent; transport availability is independent.
        return new ReplaceDatJobCreationResult(job.Id, job.Id.ToString("N"), $"/jobs/{job.Id}");
    }
}
