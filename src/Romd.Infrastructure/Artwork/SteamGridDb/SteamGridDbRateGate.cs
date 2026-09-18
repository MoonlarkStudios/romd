namespace Romd.Infrastructure.Artwork.SteamGridDb;

/// <summary>Register as singleton so provider throttling survives scoped credential/service instances.</summary>
public sealed class SteamGridDbRateGate
{
    private long _retryAfterTicks;
    public bool IsLimited => DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _retryAfterTicks);
    public void Defer(TimeSpan delay)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(delay.TotalSeconds, 1, 3600)).UtcTicks;
        long prior;
        do
        {
            prior = Interlocked.Read(ref _retryAfterTicks);
            if (prior >= until) return;
        } while (Interlocked.CompareExchange(ref _retryAfterTicks, until, prior) != prior);
    }
}
