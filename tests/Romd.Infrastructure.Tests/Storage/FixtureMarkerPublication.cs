// Block-scoped so this exact helper source can also be appended to the synthetic worker.
namespace Romd.Infrastructure.Tests.Storage
{
    internal static class FixtureMarkerPublication
    {
        internal static async Task PublishAsync(string destination, Func<Stream, Task> write)
        {
            string temporary = destination + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await write(stream);
                }
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }
}
