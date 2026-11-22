using System.Security.Claims;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Romd.Application.Common.Security;
using Romd.Infrastructure.Identity;

namespace Romd.Host.Authorization;

public sealed class CommunityDeviceAuthorization
    : IOpenIddictServerHandler<OpenIddictServerEvents.HandleDeviceAuthorizationRequestContext>
{
    public const string LabelClaim = "romd_pairing_label";

    public ValueTask HandleAsync(OpenIddictServerEvents.HandleDeviceAuthorizationRequestContext context)
    {
        if (context.Request.ClientId != RomdOpenIddictClients.CommunityDevice)
            return ValueTask.CompletedTask;

        string app = ReadLabel("app_name");
        string device = ReadLabel("device_name");
        string label = (app, device) switch
        {
            ("", "") => "Community device",
            ("", _) => device,
            (_, "") => app,
            _ => $"{app} · {device}"
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        principal.SetScopes(context.Request.GetScopes());
        principal.SetClaim(LabelClaim, label);
        context.SignIn(principal);
        return ValueTask.CompletedTask;

        // Remove formatting characters that can visually reorder or hide a name on the approval page.
        string ReadLabel(string parameter) => new string((context.Request.GetParameter(parameter)?.ToString() ?? "")
            .Where(character => !char.IsControl(character) &&
                char.GetUnicodeCategory(character) is not System.Globalization.UnicodeCategory.Format)
            .Take(58).ToArray()).Trim();
    }
}
