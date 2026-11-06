using System.Text.Json;
using System.Text.Json.Nodes;
using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ChdCdCustodyRecordTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("romd-cd-custody-").FullName;
    [Theory]
    [InlineData("encode-chd-cd")]
    [InlineData("verify-chd-cd")]
    public async Task Read_LargeCdRecord_RetainsEveryBoundedDependency(string operation)
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(_root);
        var limits = new MediaProcessingOptions { MaxConcurrent = 1 };
        var runtime = new MediaProcessingRuntime(options, limits);
        using var owner = await runtime.AcquireAsync(operation: operation);
        var record = MediaRunRecord.Read(owner.RecordPath, 0, 1).ShouldNotBeNull();
        var bindings = Enumerable.Range(0, 100).Select(i => new MediaStagingBinding(_root, i,
            Path.Combine(_root, new string('a', 120) + i), 2352, null, null, "source")).ToArray();
        record = record with { Staging = bindings };
        MediaRunRecord.Write(owner.RecordPath, record);
        new FileInfo(owner.RecordPath).Length.ShouldBeGreaterThan(MediaRunRecord.MaximumBytes);
        MediaRunRecord.Read(owner.RecordPath, 0, 1)!.Staging.ShouldBe(bindings);
        // A legacy operation cannot opt into the larger byte/count limits.
        var legacy = JsonNode.Parse(File.ReadAllText(owner.RecordPath))!;
        legacy["Operation"] = "inspect-iso";
        File.WriteAllText(owner.RecordPath, legacy.ToJsonString());
        Should.Throw<InvalidDataException>(() => MediaRunRecord.Read(owner.RecordPath, 0, 1));
        // Unknown oversized CD dependency lists also fail closed for recovery.
        MediaRunRecord.Write(owner.RecordPath, record with { Staging = [.. bindings, .. bindings.Take(29)] });
        Should.Throw<InvalidDataException>(() => MediaRunRecord.Read(owner.RecordPath, 0, 1));
        MediaRunRecord.Write(owner.RecordPath, record with { Staging = [] });
    }
    public void Dispose() => Directory.Delete(_root, true);
}
