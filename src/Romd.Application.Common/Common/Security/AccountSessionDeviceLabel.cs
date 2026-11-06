namespace Romd.Application.Common.Security;

public static class AccountSessionDeviceLabel
{
    public static string Clean(string value) =>
        new string(value.Where(character => !char.IsControl(character)).Take(120).ToArray()).Trim();
}
