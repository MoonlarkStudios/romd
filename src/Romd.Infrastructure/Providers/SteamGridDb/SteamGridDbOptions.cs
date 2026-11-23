namespace Romd.Infrastructure.Providers.SteamGridDb;

public sealed class SteamGridDbOptions
{
    public const string SectionName = "Providers:SteamGridDb";
    public string? ApiKey { get; set; }
    public bool Enabled { get; set; } = true;
}
