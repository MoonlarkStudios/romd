namespace Romd.Infrastructure.Artwork.SteamGridDb;

/// <summary>Register as singleton so provider throttling survives scoped credential/service instances.</summary>
public sealed class SteamGridDbRateGate
{
    private readonly TimeProvider _timeProvider;
    private long _retryAfterTicks;

    public SteamGridDbRateGate(TimeProvider? timeProvider = null) => _timeProvider = timeProvider ?? TimeProvider.System;

    public DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();
    public DateTimeOffset RetryAfterUtc => new(Interlocked.Read(ref _retryAfterTicks), TimeSpan.Zero);
    public bool IsLimited => GetUtcNow().UtcTicks < Interlocked.Read(ref _retryAfterTicks);
    public void Defer(TimeSpan delay)
    {
        var until = GetUtcNow().AddSeconds(Math.Clamp(delay.TotalSeconds, 1, 3600)).UtcTicks;
        long prior;
        do
        {
            prior = Interlocked.Read(ref _retryAfterTicks);
            if (prior >= until)
                return;
        } while (Interlocked.CompareExchange(ref _retryAfterTicks, until, prior) != prior);
    }
}
