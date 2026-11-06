using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Serilog.AspNetCore;
using Serilog.Core;
using Serilog.Events;
using Shouldly;

namespace Romd.Hosting.IntegrationTests.Infrastructure;

/// <summary>
///     Captures every Serilog event a host writes. Register it as an <see cref="ILogEventSink" />
///     service; the hosts' <c>ReadFrom.Services</c> Serilog configuration picks it up.
/// </summary>
internal sealed class CapturingLogSink : ILogEventSink
{
    public const string RequestLoggingSource = "Serilog.AspNetCore.RequestLoggingMiddleware";

    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

    // Completion logging otherwise resolves process-global Log.Logger, which another test
    // host's delayed entry-point teardown can replace. Keep the real configured host logger.
    public static IServiceCollection Register(IServiceCollection services, CapturingLogSink sink)
    {
        services.AddSingleton<ILogEventSink>(sink);
        services.AddOptions<RequestLoggingOptions>()
            .Configure<Serilog.ILogger>((options, logger) => options.Logger = logger);
        return services;
    }

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    /// <summary>The request log is written after the response completes, so it may trail it.</summary>
    public async Task<LogEvent> WaitForSingleAsync(Func<LogEvent, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!_events.Any(predicate) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        return _events.Where(predicate).ShouldHaveSingleItem();
    }

    public Task<LogEvent> WaitForRequestLogAsync() =>
        WaitForSingleAsync(logEvent => logEvent.Properties.TryGetValue("SourceContext", out var source)
            && source is ScalarValue { Value: RequestLoggingSource });
}
