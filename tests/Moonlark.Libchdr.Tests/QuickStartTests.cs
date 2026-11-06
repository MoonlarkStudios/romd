using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Qualifies that the package README's quick start is exactly code this project compiles and runs.</summary>
public sealed class QuickStartTests
{
    private const string Indent = "        ";

    /// <summary>The README's C# block is the library's namespace import, a blank line and the statements marked in
    /// <see cref="QuickStart"/>, which compile here and verify a pinned-tool synthetic fixture.</summary>
    [Fact]
    public void ReadmeQuickStartIsCompiledAndVerifiesAFixture()
    {
        string root = TestRepository.Root;
        string[] readme = File.ReadAllLines(Path.Combine(root, "src", "Moonlark.Libchdr", "README.md"));
        string[] source = File.ReadAllLines(Path.Combine(root, "tests", "Moonlark.Libchdr.Tests", "QuickStartTests.cs"));
        string[] documented = Between(readme, "```csharp", "```");
        // The engineering standard asks for a five-line quick start: one import and four statements.
        Assert.Equal(6, documented.Length);
        Assert.Equal("using Moonlark.Libchdr;", documented[0]);
        Assert.Equal("", documented[1]);
        string[] compiled = Between(source, Indent + "// quick-start:begin", Indent + "// quick-start:end");
        Assert.All(compiled, line => Assert.StartsWith(Indent, line, StringComparison.Ordinal));
        Assert.Equal(documented[2..], compiled.Select(line => line[Indent.Length..]));
        Assert.True(QuickStart([NativeTestEnvironment.Fixture("dvd-zstd.chd")]));
    }

    /// <summary>The README statements, with the program's arguments as a parameter.</summary>
    private static bool QuickStart(string[] args)
    {
        // quick-start:begin
        using ChdFile chd = ChdFile.Open(args[0]);
        byte[] hunk = new byte[chd.Header.HunkBytes];
        chd.ReadHunk(0, hunk);
        bool verified = ChdIntegrity.Verify(chd).IsVerified;
        // quick-start:end
        return verified;
    }

    private static string[] Between(string[] lines, string begin, string end)
    {
        int start = Array.IndexOf(lines, begin);
        Assert.True(start >= 0, $"missing '{begin}'");
        int stop = Array.IndexOf(lines, end, start + 1);
        Assert.True(stop > start, $"missing '{end}' after '{begin}'");
        return lines[(start + 1)..stop];
    }
}
