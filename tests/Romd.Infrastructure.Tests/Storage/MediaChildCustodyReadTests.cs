using System.Reflection;
using System.Runtime.Loader;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class MediaChildCustodyReadTests
{
    [Theory]
    [InlineData(16384)]
    [InlineData(1 << 20)]
    public async Task ReadRecordAsync_FileGrowsAfterInspection_RejectsBeyondBoundBeforeParsing(int maximumBytes)
    {
        string directory = Directory.CreateTempSubdirectory("romd-child-custody-read-").FullName;
        try
        {
            string path = Path.Combine(directory, "run-0.json");
            File.WriteAllBytes(path, new byte[16]);
            await Should.ThrowAsync<InvalidDataException>(() => ReadAsync(path, () =>
            {
                using var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                append.Write(new byte[maximumBytes]);
                return Task.CompletedTask;
            }, maximumBytes));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(16384)]
    [InlineData(1 << 20)]
    public async Task ReadRecordAsync_PathReplacedAfterInspection_ReadsOnlyOriginallyOpenedRecord(int maximumBytes)
    {
        string directory = Directory.CreateTempSubdirectory("romd-child-custody-path-").FullName;
        try
        {
            string path = Path.Combine(directory, "run-0.json");
            byte[] original = [1, 2, 3, 4];
            File.WriteAllBytes(path, original);
            var result = await ReadAsync(path, () =>
            {
                string replacement = path + ".replacement";
                File.WriteAllBytes(replacement, [5, 6, 7, 8]);
                File.Move(replacement, path, overwrite: true);
                return Task.CompletedTask;
            }, maximumBytes);
            result.ShouldBe(original, "the size check and read must refer to one file handle");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<byte[]> ReadAsync(string path, Func<Task> inspected, int maximumBytes)
    {
        var context = new AssemblyLoadContext("romd-child-custody-read", isCollectible: true);
        try
        {
            var child = context.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "media-worker", "Romd.Media.Worker.dll"));
            var method = child.GetType("NativeRunGuard", throwOnError: true)!.GetMethod("ReadRecordAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            return await (Task<byte[]>)method.Invoke(null, [path, CancellationToken.None, inspected, maximumBytes])!;
        }
        finally { context.Unload(); }
    }
}
