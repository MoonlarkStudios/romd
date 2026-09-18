using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Persistence;
using Romd.Infrastructure.Source;

namespace Romd.Hosting.IntegrationTests.Infrastructure;

public static class TestHelpers
{
    /// <summary>
    ///     Builds the canonical catalog projection for the given platforms from seeded source data
    ///     (DatGames + DatRoms + TitleSourceLinks) and returns a map of DatGameId to the resulting
    ///     CatalogReleaseId. Seeds that hand-craft <c>MaterializedLibraryReleases</c> use this to
    ///     stamp each row with its stable release id, mirroring the real ingest→rebuild pipeline.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, int>> BuildCatalogAndMapReleasesAsync(
        RomdDbContext context,
        params int[] platformIds)
    {
        var service = new CatalogProjectionService(
            context,
            new CatalogSourceSnapshotReader([new DatCatalogSourceSnapshotProvider(context)]),
            new TitlePayloadAvailabilityProjection(
                context,
                new CatalogPayloadAssertionReader(context, [new DatCatalogPayloadAssertionProvider(context)])),
            TimeProvider.System,
            NullLogger<CatalogProjectionService>.Instance);

        foreach (int platformId in platformIds)
        {
            await service.RebuildPlatformAsync(platformId);
        }

        return await context.CatalogReleaseSources
            .AsNoTracking()
            .Join(
                context.DatGames,
                source => source.SourceEntryId,
                game => game.SourceEntryId,
                (source, game) => new { game.Id, source.CatalogReleaseId })
            .ToDictionaryAsync(pair => pair.Id, pair => pair.CatalogReleaseId);
    }

    /// <summary>
    ///     Creates multipart form content for file upload tests.
    /// </summary>
    public static MultipartFormDataContent CreateMultipartContent(string content, string fileName)
    {
        var multipartContent = new MultipartFormDataContent();
        var fileContent = new StringContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        multipartContent.Add(fileContent, "file", fileName);
        return multipartContent;
    }

    /// <summary>
    ///     Waits for a background job to complete by polling the job status endpoint.
    /// </summary>
    public static async Task WaitForJobCompletionAsync(
        HttpClient client,
        string jobId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        const int maxDelay = 200;
        const int delayIncrement = 20;

        timeout ??= TimeSpan.FromSeconds(30);
        var start = DateTime.UtcNow;
        string? lastPhase = null;
        string? lastDetails = null;

        int delay = 10;

        while (DateTime.UtcNow - start < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await client.GetAsync($"/api/jobs/{jobId}", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                string? phase = root.GetProperty("phase").GetString();

                if (phase != lastPhase)
                {
                    lastPhase = phase;
                }

                if (root.TryGetProperty("currentItem", out var step))
                {
                    lastDetails = step.GetString();
                }

                // Check for terminal states
                if (phase is "Completed" or "CompletedWithErrors" or "Failed" or "Cancelled")
                {
                    if (phase == "Failed")
                    {
                        string? errorMsg = root.TryGetProperty("errorMessage", out var err)
                            ? err.GetString()
                            : $"No error message. Last details: {lastDetails}";
                        throw new Exception($"Job {jobId} failed: {errorMsg}");
                    }

                    return;
                }
            }

            await Task.Delay(delay, cancellationToken);
            delay += delayIncrement;
            if (delay > maxDelay)
            {
                delay = maxDelay;
            }
        }

        throw new TimeoutException(
            $"Job {jobId} did not complete within {timeout.Value.TotalSeconds} seconds. " +
            $"Last phase: {lastPhase ?? "unknown"}, Details: {lastDetails ?? "none"}");
    }

    /// <summary>
    ///     Gets a DAT ID by name from the API.
    /// </summary>
    public static async Task<string> GetDatIdByNameAsync(
        HttpClient client,
        string name,
        CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync("/api/dats", cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.GetProperty("name").GetString() == name)
            {
                return element.GetProperty("id").GetString()!;
            }
        }

        throw new InvalidOperationException($"DAT with name '{name}' not found.");
    }

    /// <summary>
    ///     Gets the first available platform ID from the API.
    ///     Platforms are seeded at startup, so this should always return a valid ID.
    /// </summary>
    public static async Task<string> GetFirstSystemKeyAsync(
        HttpClient client,
        CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync("/api/systems", cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);

        var platforms = doc.RootElement;
        if (platforms.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("No platforms found. Platforms should be seeded at startup.");
        }

        return platforms[0].GetProperty("key").GetString()!;
    }

    /// <summary>
    ///     Uploads a DAT file with a platform ID and waits for completion.
    ///     This is the correct way to upload DATs for catalog search tests,
    ///     as titles are only created when a platform is specified.
    /// </summary>
    public static async Task<string> UploadDatWithPlatformAsync(
        HttpClient client,
        string datContent,
        string fileName,
        string platformId,
        CancellationToken cancellationToken = default)
    {
        using var content = CreateMultipartContent(datContent, fileName);
        var response = await client.PostAsync($"/api/upload/dat?systemKey={platformId}", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        string jobId = doc.RootElement.GetProperty("jobId").GetString()!;

        await WaitForJobCompletionAsync(client, jobId, cancellationToken: cancellationToken);

        // Verify job completed successfully with expected results
        var jobResponse = await client.GetAsync($"/api/jobs/{jobId}", cancellationToken);
        jobResponse.EnsureSuccessStatusCode();
        string jobBody = await jobResponse.Content.ReadAsStringAsync(cancellationToken);
        using var jobDoc = JsonDocument.Parse(jobBody);
        var jobRoot = jobDoc.RootElement;

        // Check that at least one DAT was processed
        int datsSucceeded = jobRoot.GetProperty("datsSucceeded").GetInt32();
        if (datsSucceeded == 0)
        {
            throw new InvalidOperationException(
                $"DAT upload failed: no DATs were successfully processed. Job details: {jobBody}");
        }

        return jobId;
    }
}
