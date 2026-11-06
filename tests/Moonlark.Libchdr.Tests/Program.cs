namespace Moonlark.Libchdr.Tests;

/// <summary>Commands for the libchdr fuzz gate (scripts/native/fuzz.sh): writes <see cref="MalformedCorpus"/> as seed files,
/// and checks the harness's replay log against Moonlark.Libchdr with <see cref="GuardParity"/>.</summary>
/// <remarks>The test runner never calls this entry point; <c>dotnet run --project tests/Moonlark.Libchdr.Tests --
/// export-malformed-corpus &lt;directory&gt;</c> or <c>-- guard-parity &lt;harness-log&gt; &lt;seeds-directory&gt;</c> does.</remarks>
internal static class Program
{
    private static int Main(string[] args) => args switch
    {
        ["export-malformed-corpus", string directory] => ExportMalformedCorpus(directory),
        ["guard-parity", string harnessLog, string seeds] => GuardParity.Run(harnessLog, seeds, Console.Out),
        _ => Usage(),
    };

    private static int ExportMalformedCorpus(string directory)
    {
        Directory.CreateDirectory(directory);
        int count = 0;
        foreach ((string name, byte[] bytes) in MalformedCorpus.Build())
        {
            File.WriteAllBytes(Path.Combine(directory, name + ".chd"), bytes);
            count++;
        }
        Console.WriteLine($"wrote {count} malformed corpus inputs to {Path.GetFullPath(directory)}");
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: Moonlark.Libchdr.Tests export-malformed-corpus <directory>");
        Console.Error.WriteLine("       Moonlark.Libchdr.Tests guard-parity <harness-log> <seeds-directory>");
        return 2;
    }
}
