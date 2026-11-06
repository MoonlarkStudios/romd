using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Hosting.IntegrationTests.Vfs;
using Romd.Hosting.Vfs;
using Romd.Mount;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ConsumerIdentityEndpointsTests
{
    [VfsSustainedFact]
    public async Task Vfs_SustainedSeparateLibraries_ProgressWhileOneAccountExceedsItsLimit()
    {
        int seconds = int.Parse(Environment.GetEnvironmentVariable("ROMD_VFS_SUSTAINED_SECONDS") ?? "60",
            System.Globalization.CultureInfo.InvariantCulture);
        if (seconds is < 10 or > 300)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        var succeeded = new long[4];
        var rejected = new long[4];
        var payloadBytes = new long[4];
        var latency = Enumerable.Range(0, 4).Select(_ => new List<double>()).ToArray();
        var samples = new List<object>();
        long[]? halfway = null;
        bool completed = false;
        string? failureType = null;
        long? contentBytes = null;
        string? contentKind = null;
        long? started = null;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var initialCpu = process.TotalProcessorTime;
        long initialAllocations = GC.GetTotalAllocatedBytes();
        await VfsProbeEvidence.RunAsync(async _ =>
        {
            try
            {
                await using var fixture = await ConsumerHostFixture.CreateAsync(services =>
                {
                    services.AddSingleton(new ConsumerVfsOptions { AllowDevelopmentBearer = true, Enabled = true });
                    services.Configure<ContentStoreOptions>(options => options.WritePolicy = ContentWritePolicy.Raw);
                }, networkListener: true);
                await using var source = await VfsProbeContent.CreateAsync();
                contentBytes = source.Length;
                contentKind = source.Kind;
                using var client = fixture.Factory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(20);
                using var identity = new MountIdentityClient(client.BaseAddress!, allowLoopbackHttp: true);
                var tokens = new List<string>();
                int firstLibrary = 0;
                string path = "";
                for (int user = 0; user < 4; user++)
                {
                    int library = await fixture.CreateLibraryAsync("Sustained " + user);
                    if (user == 0)
                    {
                        firstLibrary = library;
                        string relative = await fixture.SeedVfsProbeAsync(library, source);
                        path = "/vfs/library/" + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
                    }
                    else
                        await fixture.ShareProbeReleaseAsync(firstLibrary, library);
                    string name = "sustained-" + user;
                    await fixture.CreateConsumerUserAsync(name, name + "@localhost", "Password123", account => account.LibraryId = library);
                    var login = await identity.BeginLoginAsync();
                    using var approval = await client.PostAsync("/connect/verify", await BrowserAuthenticationForm.CreateAsync(client, "/connect/verify", new Dictionary<string, string>
                    {
                        ["user_code"] = login.UserCode,
                        ["login"] = name,
                        ["password"] = "Password123"
                    }));
                    approval.StatusCode.ShouldBe(HttpStatusCode.OK);
                    tokens.Add((await identity.PollLoginAsync(login.DeviceCode)).AccessToken);
                }
                var random = new Random(1729);
                var reads = Enumerable.Range(0, 128).Select(index =>
                {
                    int bytes = index % 2 == 0 ? 4096 : 65536;
                    long offset = source.NextOffset(random, bytes);
                    return (Offset: offset, Bytes: source.ReadRange(offset, bytes));
                }).ToArray();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 40));
                process.Refresh();
                initialCpu = process.TotalProcessorTime;
                initialAllocations = GC.GetTotalAllocatedBytes();
                started = Stopwatch.GetTimestamp();
                Task Worker(int user, int worker) => Task.Run(async () =>
                {
                    try
                    {
                        int index = worker;
                        while (Stopwatch.GetElapsedTime(started.Value).TotalSeconds < seconds)
                        {
                            var range = reads[index++ % reads.Length];
                            using var request = new HttpRequestMessage(HttpMethod.Get, path);
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens[user]);
                            request.Headers.Range = new RangeHeaderValue(range.Offset, range.Offset + range.Bytes.Length - 1);
                            long requestStarted = Stopwatch.GetTimestamp();
                            using var response = await client.SendAsync(request, deadline.Token);
                            byte[] actual = await response.Content.ReadAsByteArrayAsync(deadline.Token);
                            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                            {
                                response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(1));
                                actual.ShouldBeEmpty();
                                Interlocked.Increment(ref rejected[user]);
                                await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
                            }
                            else
                            {
                                response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
                                actual.AsSpan().SequenceEqual(range.Bytes).ShouldBeTrue();
                                Interlocked.Increment(ref succeeded[user]);
                                Interlocked.Add(ref payloadBytes[user], actual.Length);
                                lock (latency[user])
                                {
                                    if (latency[user].Count < 250_000)
                                        latency[user].Add(Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds);
                                }
                            }
                        }
                    }
                    catch
                    {
                        await deadline.CancelAsync();
                        throw;
                    }
                }, deadline.Token);
                Task workload = Task.WhenAll(Enumerable.Range(0, 4).SelectMany(user =>
                    Enumerable.Range(0, user == 0 ? 8 : 1).Select(worker => Worker(user, worker))));
                try
                {
                    while (!workload.IsCompleted)
                    {
                        await Task.WhenAny(workload, Task.Delay(TimeSpan.FromSeconds(1)));
                        process.Refresh();
                        long[] progress = Enumerable.Range(0, succeeded.Length).Select(user => Interlocked.Read(ref succeeded[user])).ToArray();
                        double sampledSeconds = Stopwatch.GetElapsedTime(started.Value).TotalSeconds;
                        if (halfway is null && sampledSeconds >= seconds / 2d)
                            halfway = progress;
                        samples.Add(new
                        {
                            elapsedSeconds = sampledSeconds,
                            combinedWorkingSetBytes = process.WorkingSet64,
                            succeeded = progress
                        });
                    }
                    await workload;
                    halfway.ShouldNotBeNull();
                    halfway.ShouldAllBe(value => value > 0, "every account must progress during the first half of contention");
                    for (int user = 0; user < 4; user++)
                        succeeded[user].ShouldBeGreaterThan(halfway[user], "every account must continue progressing during the second half");
                    rejected.Skip(1).ShouldAllBe(value => value == 0);
                }
                finally
                {
                    await deadline.CancelAsync();
                    try
                    { await workload; }
                    catch (Exception) when (workload.IsFaulted || workload.IsCanceled)
                    {
                        // The action has already observed its failure. Join all workers before
                        // source/host disposal and evidence serialization, preserving that error.
                    }
                }
                completed = true;
                return true;
            }
            catch (Exception failure)
            {
                completed = false;
                failureType = failure.GetType().FullName;
                throw;
            }
        }, async token =>
        {
            double? elapsed = started is { } began ? Stopwatch.GetElapsedTime(began).TotalSeconds : null;
            process.Refresh();
            var users = Enumerable.Range(0, 4).Select(user =>
            {
                latency[user].Sort();
                double? Percentile(double percentile) => latency[user].Count == 0 ? null :
                    latency[user][(int)Math.Ceiling(latency[user].Count * percentile) - 1];
                return new
                {
                    account = user,
                    concurrency = user == 0 ? 8 : 1,
                    succeeded = succeeded[user],
                    rejected = rejected[user],
                    successfulRequestsPerSecond = elapsed is > 0 ? succeeded[user] / elapsed.Value : (double?)null,
                    successfulPayloadBytes = payloadBytes[user],
                    latencySamples = latency[user].Count,
                    p50Milliseconds = Percentile(.5),
                    p95Milliseconds = Percentile(.95),
                    p99Milliseconds = Percentile(.99)
                };
            }).ToArray();
            await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("ROMD_VFS_SUSTAINED_REPORT")!, JsonSerializer.Serialize(new
            {
                recordedAt = DateTimeOffset.UtcNow,
                status = completed ? "passed" : "failed",
                failureType,
                targetSeconds = seconds,
                elapsedSeconds = elapsed,
                contentBytes,
                contentKind,
                libraries = 4,
                storage = "raw",
                globalAdmission = 8,
                perAccountAdmission = 4,
                users,
                halfway,
                combinedProcessCpuSeconds = (process.TotalProcessorTime - initialCpu).TotalSeconds,
                combinedProcessAllocatedBytes = GC.GetTotalAllocatedBytes() - initialAllocations,
                samples,
                limitations = "Loopback HTTP, four assigned libraries sharing one authorized release/CAS object; not a cross-library isolation test. Eight workers on account 0, one on each other account. Successful bounded queuing need not reject requests. 503 honors Retry-After before trying a new range. Mixed 4/64 KiB reads with byte verification. OS caches warmed by seeding and hashing; normal host logging enabled. CPU, allocations and working set include test client/server/log capture, exclude PostgreSQL. Latency retains first 250,000 successes per account. One run, not a long soak, mounted-client test, WAN or isolated server resource measurement."
            }, new JsonSerializerOptions { WriteIndented = true }), token);
        }, _ => Task.CompletedTask, diagnostic => Console.Error.WriteLine(diagnostic), CancellationToken.None);
    }

    private sealed partial class ConsumerHostFixture
    {
        public async Task ShareProbeReleaseAsync(int sourceLibrary, int targetLibrary)
        {
            await using var db = CreateSetupDbContext();
            var entry = await db.MaterializedLibraryReleases.AsNoTracking().SingleAsync(row => row.LibraryId == sourceLibrary);
            entry.Id = 0;
            entry.LibraryId = targetLibrary;
            db.MaterializedLibraryReleases.Add(entry);
            await db.SaveChangesAsync();
        }
    }

    public sealed class VfsSustainedFactAttribute : FactAttribute
    {
        public VfsSustainedFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ROMD_VFS_SUSTAINED_REPORT")))
                Skip = "Set ROMD_VFS_SUSTAINED_REPORT for the opt-in multi-library load probe.";
        }
    }
}
