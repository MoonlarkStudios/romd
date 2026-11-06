using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Romd.Hosting.IntegrationTests.Vfs;

/// <summary>Opt-in request latency sensitivity probe, not a TCP/WAN simulator.</summary>
internal sealed class VfsDelayStartupFilter(int milliseconds) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            if (context.Request.Path.StartsWithSegments("/vfs"))
                await Task.Delay(milliseconds, context.RequestAborted);
            await continuation(context);
        });
        next(app);
    };
}
