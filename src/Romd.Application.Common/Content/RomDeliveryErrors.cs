using ErrorOr;

namespace Romd.Application.Common.Content;

public static class RomDeliveryErrors
{
    public static Error InvalidFormat() =>
        Error.Validation("Delivery.InvalidFormat", "format must be exactly 'original' or 'rvz'.");
}
