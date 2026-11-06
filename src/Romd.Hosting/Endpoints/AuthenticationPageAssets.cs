using System.Text;

namespace Romd.Host.Endpoints;

/// <summary>Self-contained authentication uses the portals' canonical theme and brand assets.</summary>
internal static class AuthenticationPageAssets
{
    internal static readonly string Head = $"<script>{Read("Romd.Hosting.Theme.Bootstrap.js")}</script>"
        + $"<style>{Read("Romd.Hosting.Theme.Portal.css")}\n{Read("Romd.Hosting.Theme.Auth.css")}</style>";

    internal static readonly string Brand = $$"""
        <h1 class="romd-auth-brand" aria-label="ROMD">
          <img class="romd-auth-brand-light" src="{{Image("Romd.Hosting.Brand.RomdLockup.svg")}}" alt="" width="320" height="85">
          <img class="romd-auth-brand-dark" src="{{Image("Romd.Hosting.Brand.RomdLockupDark.svg")}}" alt="" width="320" height="85">
        </h1>
        """;

    private static string Image(string name) =>
        "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(Read(name)));

    private static string Read(string name)
    {
        using var stream = typeof(AuthenticationPageAssets).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The authentication asset '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
