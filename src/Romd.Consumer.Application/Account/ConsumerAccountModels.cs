using Romd.Consumer.Application.Browse;

namespace Romd.Consumer.Application.Account;

public sealed record ConsumerAccountSettings(
    string Theme,
    ConsumerReleasePreference ReleasePreference)
{
    public ConsumerAccountSettings(string theme)
        : this(theme, ConsumerReleasePreference.Default)
    {
    }
}
