using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Hosting.IntegrationTests.Vfs;
using Romd.Hosting.Vfs;
using Romd.Infrastructure.Identity;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerIdentityEndpointsTests
{
    [VfsLoadFact]
    public async Task Vfs_ConcurrentRawCasRanges_RecordAuthenticatedLoopbackLoad()
    {
        string reportPath = Environment.GetEnvironmentVariable("ROMD_VFS_LOAD_REPORT")!;
        await using var fixture = await ConsumerHostFixture.CreateAsync(services =>
        {
            services.AddSingleton(new ConsumerVfsOptions { AllowDevelopmentBearer = true, Enabled = true, MaximumConcurrentRequests = 8 });
            services.Configure<ContentStoreOptions>(options => options.WritePolicy = ContentWritePolicy.Raw);
        }, networkListener: true);
        fixture.Factory.Services.GetRequiredService<IOptions<ContentStoreOptions>>().Value.WritePolicy
            .ShouldBe(ContentWritePolicy.Raw);
        int library = await fixture.CreateLibraryAsync("Load probe");
        await using var source = await VfsProbeContent.CreateAsync();
        string relativePath = await fixture.SeedVfsProbeAsync(library, source);
        string path = "/vfs/library/" + string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));
        using var client = fixture.Factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        var tokens = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            string name = "load-" + i;
            await fixture.CreateConsumerUserAsync(name, name + "@localhost", "Password123", user => user.LibraryId = library);
            using var device = await client.PostAsync("/connect/device", CreateFormContent(new Dictionary<string, string>
            {
                ["client_id"] = RomdOpenIddictClients.Mount,
                ["scope"] = "offline_access email profile roles"
            }));
            device.StatusCode.ShouldBe(HttpStatusCode.OK);
            var codes = await ReadJsonObjectAsync(device);
            using var approval = await client.PostAsync("/connect/verify", await BrowserAuthenticationForm.CreateAsync(client, "/connect/verify", new Dictionary<string, string>
            {
                ["user_code"] = RequiredString(codes, "user_code"),
                ["login"] = name,
                ["password"] = "Password123"
            }));
            approval.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var token = await client.PostAsync("/connect/token",
                CreateDeviceTokenRequest(RequiredString(codes, "device_code"), RomdOpenIddictClients.Mount));
            token.StatusCode.ShouldBe(HttpStatusCode.OK);
            tokens.Add(RequiredString(await ReadJsonObjectAsync(token), "access_token"));
        }

        var runs = new List<object>();
        foreach (int bytes in new[] { 4096, 65536 })
        {
            var random = new Random(1729);
            // Prepare expected bytes before timing: source.Stream has a shared cursor.
            var reads = Enumerable.Range(0, 128).Select(_ =>
            {
                long offset = source.NextOffset(random, bytes);
                return (Offset: offset, Expected: source.ReadRange(offset, bytes));
            }).ToArray();
            foreach (int concurrency in new[] { 1, 4, 8, 16 })
                for (int repeat = 0; repeat < 3; repeat++)
                {
                    using var metrics = new VfsProbeMetrics();
                    using var process = Process.GetCurrentProcess();
                    process.Refresh();
                    long workingSetStart = process.WorkingSet64;
                    TimeSpan cpuStart = process.TotalProcessorTime;
                    long allocatedStart = GC.GetTotalAllocatedBytes();
                    long peakWorkingSet = workingSetStart;
                    using var sampling = new CancellationTokenSource();
                    Task sample = Task.Run(async () =>
                    {
                        try
                        {
                            while (true)
                            {
                                await Task.Delay(20, sampling.Token);
                                process.Refresh();
                                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                            }
                        }
                        catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
                    });
                    var measurements = new (bool Success, double Milliseconds)[reads.Length];
                    long started = Stopwatch.GetTimestamp();
                    try
                    {
                        await Parallel.ForEachAsync(Enumerable.Range(0, reads.Length),
                            new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (index, ct) =>
                            {
                                var read = reads[index];
                                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens[index % tokens.Count]);
                                request.Headers.Range = new RangeHeaderValue(read.Offset, read.Offset + bytes - 1);
                                long requestStart = Stopwatch.GetTimestamp();
                                using var response = await client.SendAsync(request, ct);
                                byte[] actual = await response.Content.ReadAsByteArrayAsync(ct);
                                measurements[index] = (response.StatusCode == HttpStatusCode.PartialContent,
                                    Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds);
                                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                                {
                                    response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(1));
                                    actual.ShouldBeEmpty();
                                }
                                else
                                {
                                    response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
                                    actual.AsSpan().SequenceEqual(read.Expected).ShouldBeTrue("Range bytes must match the source image.");
                                }
                            });
                    }
                    finally { sampling.Cancel(); await sample; }
                    double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                    process.Refresh();
                    double[] success = measurements.Where(value => value.Success).Select(value => value.Milliseconds).Order().ToArray();
                    success.ShouldNotBeEmpty();
                    if (concurrency <= 8)
                        success.Length.ShouldBe(reads.Length);
                    runs.Add(new
                    {
                        bytes,
                        concurrency,
                        repeat,
                        attempts = reads.Length,
                        succeeded = success.Length,
                        rejected = reads.Length - success.Length,
                        elapsedSeconds = seconds,
                        successfulRequestsPerSecond = success.Length / seconds,
                        successfulMiBPerSecond = success.Length * (double)bytes / 1048576 / seconds,
                        p50Milliseconds = success[(int)Math.Ceiling(success.Length * .50) - 1],
                        p95Milliseconds = success[(int)Math.Ceiling(success.Length * .95) - 1],
                        p99Milliseconds = success[(int)Math.Ceiling(success.Length * .99) - 1],
                        combinedProcessCpuMilliseconds = (process.TotalProcessorTime - cpuStart).TotalMilliseconds,
                        combinedProcessAllocatedBytes = GC.GetTotalAllocatedBytes() - allocatedStart,
                        combinedProcessWorkingSetStart = workingSetStart,
                        combinedProcessWorkingSetEnd = process.WorkingSet64,
                        combinedProcessSampledPeakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64),
                        server = metrics.Snapshot()
                    });
                }
        }
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            recordedAt = DateTimeOffset.UtcNow,
            contentBytes = source.Length,
            contentKind = source.Kind,
            storage = "raw",
            authenticatedUsers = tokens.Count,
            admissionLimit = 8,
            environment = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            limitations = "Loopback HTTP, one shared library/file; OS caches warmed by hashing/seeding and uncontrolled; no retries; ascending concurrency, three repeated batches; CPU/allocation/working set include both host and client in test process, exclude PostgreSQL; throughput includes byte verification and normal test-host logging; memory sampled every 20 ms, not a hard peak; not mounted or WAN performance.",
            runs
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public sealed class VfsLoadFactAttribute : FactAttribute
    {
        public VfsLoadFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ROMD_VFS_LOAD_REPORT")))
                Skip = "Set ROMD_VFS_LOAD_REPORT to run the opt-in authenticated CAS load measurement.";
        }
    }
}
