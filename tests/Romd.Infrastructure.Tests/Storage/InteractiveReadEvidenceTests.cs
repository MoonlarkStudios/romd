using System.Text.Json;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Media.Probe;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class InteractiveReadEvidenceTests
{
    [Fact]
    public void Report_CapturedSnapshot_LaterOutcomesAndBoundariesCannotChangeIt()
    {
        var identity = new RomCdFileIdentity(2352, Sha256.Parse(new string('a', 64)), Sha1.Parse(new string('b', 40)),
            Md5.Parse(new string('c', 32)), Crc32.FromUInt32(1));
        var member = new ChdCdReadCatalog.Member(1, 0, "Audio", "/vfs/file", "/readonly/oracle.bin", identity);
        var sample = new ChdCdInteractiveReadChecks.Case(member, 4096, "start", new(member.Url, identity.Size, identity.Sha256, 0, 2352, new byte[2352]));
        var evidence = new InteractiveReadEvidence([sample]);
        evidence.BeginRead(sample, "loaded");
        evidence.Record(sample, "loaded", new("completed", 1, 0, 0, 1, 2, 2352, null, []), 0);
        object snapshot = evidence.Report();
        string captured = JsonSerializer.Serialize(snapshot);

        var later = sample with { Boundary = "hunk" };
        evidence.BeginRead(later, "loaded");
        evidence.Record(later, "loaded", new("deadline", 1, 0, 0, null, 120, 0, "deadline", []), 0);
        evidence.Missed(later, "loaded");

        JsonSerializer.Serialize(snapshot).ShouldBe(captured, "a checkpoint must stay internally consistent after the evidence lock is released");
    }

    [Fact]
    public void Report_DeadlineMissedAndFatalReads_RemainInDenominatorOutsideSuccessfulPercentiles()
    {
        var identity = new RomCdFileIdentity(2352, Sha256.Parse(new string('a', 64)), Sha1.Parse(new string('b', 40)),
            Md5.Parse(new string('c', 32)), Crc32.FromUInt32(1));
        var member = new ChdCdReadCatalog.Member(1, 0, "Audio", "/vfs/file", "/readonly/oracle.bin", identity);
        var sample = new ChdCdInteractiveReadChecks.Case(member, 4096, "eof", new(member.Url, identity.Size, identity.Sha256, 2351, 4096, [1]));
        var evidence = new InteractiveReadEvidence([sample]);
        evidence.BeginRead(sample, "loaded");
        evidence.Record(sample, "loaded", new("completed", 2, 1, 10, 20, 30, 1, null, []), 5);
        evidence.BeginRead(sample, "loaded");
        evidence.Record(sample, "loaded", new("deadline", 3, 3, 100, null, 120, 0, "deadline", []), 0);
        evidence.Missed(sample, "loaded");
        evidence.BeginRead(sample, "loaded");
        evidence.Fail(sample, "loaded", "fatal-integrity-or-protocol-failure");

        using var report = JsonDocument.Parse(JsonSerializer.Serialize(evidence.Report()));
        var group = report.RootElement.GetProperty("Groups").GetProperty("loaded/disc-1/Audio/4096");
        group.GetProperty("ObservedArrivals").GetInt64().ShouldBe(4);
        group.GetProperty("InFlight").GetInt64().ShouldBe(0);
        group.GetProperty("Outcomes").EnumerateObject().ShouldAllBe(property => property.Value.GetInt32() == 1);
        group.GetProperty("CompletionMilliseconds").GetProperty("Count").GetInt32().ShouldBe(1);
        group.GetProperty("CompletionMilliseconds").GetProperty("MaximumMilliseconds").GetDouble().ShouldBe(30);
        group.GetProperty("ScheduledCompletionMilliseconds").GetProperty("MaximumMilliseconds").GetDouble().ShouldBe(35);
        group.GetProperty("FailureElapsedMilliseconds").GetProperty("MaximumMilliseconds").GetDouble().ShouldBe(120);
        group.GetProperty("Worst").GetProperty("Outcome").GetString().ShouldBe("deadline");
    }
}
