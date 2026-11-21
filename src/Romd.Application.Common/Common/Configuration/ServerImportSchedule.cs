namespace Romd.Application.Common.Configuration;

public interface IServerImportOptions
{
    string? ServerImportDirectory { get; }
    int? ServerImportIntervalMinutes { get; }
}

public static class ServerImportSchedule
{
    public static string? ToCron(int minutes) => minutes switch
    {
        5 => "*/5 * * * *",
        15 => "*/15 * * * *",
        30 => "*/30 * * * *",
        60 => "0 * * * *",
        _ => null
    };

    public static DateTimeOffset? NextCheck(DateTimeOffset now, int? minutes)
    {
        if (minutes is not { } interval || ToCron(interval) is null)
            return null;
        long cadence = TimeSpan.FromMinutes(interval).Ticks;
        return new DateTimeOffset(now.UtcTicks - now.UtcTicks % cadence + cadence, TimeSpan.Zero);
    }
}
