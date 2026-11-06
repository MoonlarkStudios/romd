using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Romd.Hosting.IntegrationTests.Vfs;

/// <summary>Server-side connection aborts, not a WAN or server-process restart simulator.</summary>
internal sealed class VfsConnectionFaultProbe : IStartupFilter
{
    private int _interrupted;
    private long _gets, _aborts;
    public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public long Gets => Interlocked.Read(ref _gets);
    public long Aborts => Interlocked.Read(ref _aborts);
    public void Interrupt() => Volatile.Write(ref _interrupted, 1);
    public void Restore() => Volatile.Write(ref _interrupted, 0);

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            if (context.Request.Path.StartsWithSegments("/vfs") && context.Request.Method == "GET")
            {
                Interlocked.Increment(ref _gets);
                if (Volatile.Read(ref _interrupted) != 0)
                {
                    Interlocked.Increment(ref _aborts);
                    context.Abort();
                    Observed.TrySetResult();
                    return;
                }
            }
            await continuation(context);
        });
        next(app);
    };
}
