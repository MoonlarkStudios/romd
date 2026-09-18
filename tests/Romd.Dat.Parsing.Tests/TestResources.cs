using System.Reflection;

namespace Romd.Dat.Parsing.Tests;

internal static class TestResources
{
    private static readonly Assembly Assembly = typeof(TestResources).Assembly;

    public static Stream OpenNoIntroN64()
        => Open("nointro_nintendo_n64.dat");

    public static Stream OpenRedumpPs1()
        => Open("redump_sony_ps1.dat");

    private static Stream Open(string filename)
    {
        string resourceName = $"Romd.Dat.Parsing.Tests.Resources.{filename}";
        return Assembly.GetManifestResourceStream(resourceName)
               ?? throw new InvalidOperationException($"Resource not found: {resourceName}");
    }
}
