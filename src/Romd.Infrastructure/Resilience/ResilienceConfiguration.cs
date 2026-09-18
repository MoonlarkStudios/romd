using Microsoft.EntityFrameworkCore;
using Romd.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Romd.Admin.Application.Resilience;

namespace Romd.Infrastructure.Resilience;

/// <summary>
///     Configures Polly resilience policies for the application.
/// </summary>
public static class ResilienceConfiguration
{
    /// <summary>
    ///     Adds resilience policies to the service collection.
    /// </summary>
    public static IServiceCollection AddResiliencePolicies(this IServiceCollection services)
    {
        // TransientFault: Retry on I/O, timeout, and transient database errors
        services.AddResiliencePipeline<string>(ResiliencePolicyKey.TransientFault, builder =>
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<IOException>()
                    .Handle<TimeoutException>()
                    .Handle<DbUpdateException>(IsTransientDbError),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(200),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true
            });
        });

        // ExternalApi: Retry on HTTP failures with exponential backoff
        services.AddResiliencePipeline<string>(ResiliencePolicyKey.ExternalApi, builder =>
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true
            });
        });

        return services;
    }

    public static IHttpResiliencePipelineBuilder AddIgdbResilienceHandler(this IHttpClientBuilder builder) =>
        builder.AddResilienceHandler(ResiliencePolicyKey.IgdbApi, resilience =>
        {
            resilience
                .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
                {
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .HandleResult(r => r.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                        .HandleResult(r => (int)r.StatusCode >= 500),
                    MaxRetryAttempts = 3,
                    DelayGenerator = static args =>
                    {
                        if (args.Outcome.Result?.Headers.RetryAfter?.Delta is { } delta)
                        {
                            return ValueTask.FromResult<TimeSpan?>(delta);
                        }

                        return ValueTask.FromResult<TimeSpan?>(
                            TimeSpan.FromSeconds(Math.Pow(2, args.AttemptNumber)));
                    }
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
                {
                    SamplingDuration = TimeSpan.FromMinutes(1),
                    FailureRatio = 0.5,
                    MinimumThroughput = 10,
                    BreakDuration = TimeSpan.FromMinutes(1)
                });
        });

    /// <summary>
    ///     Determines if a database error is transient and can be retried.
    /// </summary>
    private static bool IsTransientDbError(DbUpdateException ex) => PostgreSqlTransientErrors.IsTransient(ex);
}
