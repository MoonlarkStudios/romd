namespace Romd.Infrastructure.Enrichment;

internal static class ProviderIdentityInput
{
    public static string? Parse(string value, string host, string prefix, bool allowSlug = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length is 0 or > 500) return null;
        if (value.All(char.IsAsciiDigit) && long.TryParse(value, out var id) && id > 0)
            return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host == host || uri.Host == "www." + host) || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var segment = uri.AbsolutePath[prefix.Length..].TrimEnd('/');
        if (segment.Length == 0 || !segment.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return null;
        return allowSlug ? segment : Parse(segment, host, prefix);
    }
}
