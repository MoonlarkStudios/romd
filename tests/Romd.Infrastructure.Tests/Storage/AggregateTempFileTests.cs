using System.IO.Abstractions;
using System.Security.Cryptography;
using NSubstitute;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class AggregateTempFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("romd-aggregate-staging-").FullName;
    private LocalTempFileFactory Factory(long bytes = 24, int slots = 3)
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(_root);
        options.MaxUploadBytes.Returns(16);
        return new(options, new FileSystem(), new TempStorageOptions
        { MaximumBytes = bytes, MaximumFiles = slots, MinimumFreeBytes = 0, AdmissionTimeoutSeconds = 1 });
    }
    private static Sha256 Hash(byte[] bytes) => Sha256.FromSpan(SHA256.HashData(bytes));

    [Fact]
    public async Task ReserveAggregateAsync_ThreeCharges_ReservesAllBeforePayloadAndMeasuresExactSource()
    {
        byte[] bytes = [1, 2, 3, 4];
        var result = await Factory().ReserveAggregateAsync(new(Guid.NewGuid(), "convergence", 4, 8, 3));
        result.IsError.ShouldBeFalse();
        await using var aggregate = result.Value;
        aggregate.Output.ShouldNotBeNull().MaximumBytes.ShouldBe(8);
        aggregate.Verification.ShouldNotBeNull().MaximumBytes.ShouldBe(3);
        Directory.GetFiles(_root, "*.data", SearchOption.AllDirectories).ShouldBeEmpty();
        using var ledger = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "temp", "staging-v1", "reservations.json")));
        ledger.RootElement.GetProperty("Reservations").EnumerateObject().Select(property => property.Value.GetInt64()).Order().ShouldBe(new long[] { 3, 4, 8 });
        var staged = await aggregate.MaterializeSourceAsync(new MemoryStream(bytes), Hash(bytes), bytes.Length);
        staged.IsError.ShouldBeFalse();
        var file = staged.Value;
        file.FileSha256.ShouldBe(Hash(bytes));
        file.FileSize.ShouldBe(bytes.Length);
        file.DatHashes.ShouldNotBeNull().Md5.ShouldBe(Md5.FromSpan(MD5.HashData(bytes)));
        file.DatHashes.Sha1.ShouldBe(Sha1.FromSpan(SHA1.HashData(bytes)));
        File.ReadAllBytes(file.Path).ShouldBe(bytes);
        (await aggregate.MaterializeSourceAsync(new MemoryStream(bytes), Hash(bytes), bytes.Length)).FirstError.Code.ShouldBe("Temp.SourceIdentity");
        await aggregate.DisposeAsync();
        File.Exists(file.Path).ShouldBeFalse();
        await using var recovered = (await Factory().ReserveAggregateAsync(new(Guid.NewGuid(), "convergence", 8, 8, 8))).Value;
    }

    [Theory]
    [InlineData(16, 1)]
    [InlineData(16, 3)]
    public async Task ReserveAggregateAsync_InsufficientCountOrBytes_PublishesNoPartialReservation(long bytes, int slots)
    {
        var factory = Factory(bytes, slots);
        await Should.ThrowAsync<ResourceCapacityException>(() => factory.ReserveAggregateAsync(new(Guid.NewGuid(), "convergence", 8, 9)));
        Directory.GetFiles(_root, "*.data", SearchOption.AllDirectories).ShouldBeEmpty();
        string ledger = Path.Combine(_root, "temp", "staging-v1", "reservations.json");
        if (File.Exists(ledger))
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ledger));
            json.RootElement.GetProperty("Reservations").EnumerateObject().ShouldBeEmpty();
        }
        await using var sibling = await factory.CreateAsync(new MemoryStream(new byte[16]));
        sibling.FileSize.ShouldBe(16);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("short")]
    [InlineData("long")]
    public async Task MaterializeSourceAsync_ChangedOwnedInput_ReturnsNoArtifactAndDisposalReturnsWholeBudget(string mutation)
    {
        byte[] expected = [1, 2, 3, 4];
        var factory = Factory(bytes: 16);
        var aggregate = (await factory.ReserveAggregateAsync(new(Guid.NewGuid(), "convergence", 4, 12))).Value;
        byte[] actual = mutation == "short" ? expected[..3] : mutation == "long" ? [1, 2, 3, 4, 5] : [5, 2, 3, 4];
        var result = await aggregate.MaterializeSourceAsync(new MemoryStream(actual), Hash(expected), 4);
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Temp.SourceIdentity");
        await Should.ThrowAsync<ResourceCapacityException>(() => factory.CreateAsync(new MemoryStream([1])));
        await aggregate.DisposeAsync();
        await using var sibling = await factory.CreateAsync(new MemoryStream(new byte[16]));
    }

    [Fact]
    public async Task ReserveAggregateAsync_ConcurrentHosts_OneWholeWinnerAndNoPartialLoser()
    {
        async Task<ITempFileAggregate?> AttemptAsync()
        {
            try
            { return (await Factory(bytes: 16, slots: 4).ReserveAggregateAsync(new(Guid.NewGuid(), "convergence", 4, 8))).Value; }
            catch (ResourceCapacityException) { return null; }
        }
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => AttemptAsync()));
        try
        {
            attempts.OfType<ITempFileAggregate>().Count().ShouldBe(1);
            using var ledger = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "temp", "staging-v1", "reservations.json")));
            var charges = ledger.RootElement.GetProperty("Reservations").EnumerateObject().Select(property => property.Value.GetInt64()).ToArray();
            charges.Length.ShouldBe(2);
            charges.Sum().ShouldBe(12);
        }
        finally { foreach (var aggregate in attempts.OfType<ITempFileAggregate>()) await aggregate.DisposeAsync(); }
    }

    [Fact]
    public async Task ReserveAggregateAsync_PreCanceled_NoChargeOrPayload()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => Factory().ReserveAggregateAsync(new(Guid.NewGuid(), "convergence", 4, 8), cancellation.Token));
        Directory.GetFiles(_root, "*.data", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
