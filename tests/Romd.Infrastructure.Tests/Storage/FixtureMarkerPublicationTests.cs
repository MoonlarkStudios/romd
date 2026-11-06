using System.Globalization;
using System.Text;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class FixtureMarkerPublicationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("romd-marker-publication-").FullName;

    [Theory]
    [InlineData(null)]
    [InlineData("67890")]
    public async Task PublishAsync_PartialWrite_DoesNotExposeNewMarkerUntilComplete(string? existingPid)
    {
        string marker = Path.Combine(_root, "descendant.pid");
        if (existingPid is not null)
            await File.WriteAllTextAsync(marker, existingPid);
        var partialWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task publication = FixtureMarkerPublication.PublishAsync(marker, async stream =>
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("12"));
            partialWritten.SetResult();
            await completeWrite.Task;
            await stream.WriteAsync(Encoding.UTF8.GetBytes("345"));
        });

        string? visibleDuringWrite;
        try
        {
            await partialWritten.Task.WaitAsync(TimeSpan.FromSeconds(10));
            visibleDuringWrite = File.Exists(marker) ? File.ReadAllText(marker) : null;
        }
        finally
        {
            completeWrite.TrySetResult();
            await publication.WaitAsync(TimeSpan.FromSeconds(10));
        }

        visibleDuringWrite.ShouldBe(existingPid, "readers must see only the previous complete PID until the new write completes");
        int.Parse(File.ReadAllText(marker), CultureInfo.InvariantCulture).ShouldBe(12345);
        Directory.EnumerateFiles(_root).ShouldBe([marker]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("67890")]
    public async Task PublishAsync_WriterFails_PreservesPreviousMarkerAndRemovesTemporaryFile(string? existingPid)
    {
        string marker = Path.Combine(_root, "descendant.pid");
        if (existingPid is not null)
            await File.WriteAllTextAsync(marker, existingPid);
        var failure = new InvalidOperationException("Controlled incomplete write");
        var error = await Should.ThrowAsync<InvalidOperationException>(() => FixtureMarkerPublication.PublishAsync(marker, async stream =>
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("12"));
            throw failure;
        }));

        error.ShouldBeSameAs(failure);
        if (existingPid is null)
            Directory.EnumerateFileSystemEntries(_root).ShouldBeEmpty();
        else
        {
            File.ReadAllText(marker).ShouldBe(existingPid);
            Directory.EnumerateFiles(_root).ShouldBe([marker]);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
