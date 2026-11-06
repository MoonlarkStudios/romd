using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Generation;
using Moonlark.Native.Engineering.Tests.TestSupport;
using Moonlark.Native.Engineering.Upstream;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Upstream;

/// <summary>Identity rewrites change exactly the identity values and fail closed on ambiguous or unsafe input.</summary>
public sealed class AuthorityTextTests
{
    private static readonly LibchdrPin Pin = Authorities.ReadLibchdr(TestRepository.Root).Value.Pin;
    private static readonly string PinText = File.ReadAllText(Path.Combine(TestRepository.Root, Authorities.PinPath));
    private static readonly string PropsText = File.ReadAllText(Path.Combine(TestRepository.Root, Authorities.PropsPath));

    /// <summary>At the current pin every derived authority is byte-identical, so an update to the same commit changes nothing.</summary>
    [Fact]
    public void CurrentPinRewritesAreByteIdentical()
    {
        Assert.Equal(PinText, AuthorityText.RewritePin(PinText, Pin.Commit, Pin.UpstreamVersion, Pin.Headers).Value);
        Assert.Equal(PropsText, AuthorityText.RewriteProps(PropsText, Pin.Commit, Pin.UpstreamVersion).Value);
        string header = File.ReadAllText(Path.Combine(TestRepository.Root, GenerationConfiguration.SourcePath, Authorities.HeaderPaths[0]));
        Assert.Equal(File.ReadAllText(Path.Combine(TestRepository.Root, ExportInventory.AllowlistPath)),
            UpstreamUpdate.ExportList(ExportInventory.FromHeader(header).Value));
    }

    /// <summary>New values replace only the identity substrings; inline arrays, spacing and other fields stay byte-identical.</summary>
    [Fact]
    public void RewriteKeepsFormattingAndOtherFields()
    {
        string commit = new('b', 40);
        string digest = new('c', 64);
        var headers = new Dictionary<string, string>(Pin.Headers, StringComparer.Ordinal) { [Authorities.HeaderPaths[0]] = digest };
        string expected = PinText.Replace(Pin.Commit, commit, StringComparison.Ordinal).Replace(Pin.UpstreamVersion, "v0.4.0-2-gbbbbbbb", StringComparison.Ordinal)
            .Replace(Pin.Headers[Authorities.HeaderPaths[0]], digest, StringComparison.Ordinal);
        Assert.Equal(expected, AuthorityText.RewritePin(PinText, commit, "v0.4.0-2-gbbbbbbb", headers).Value);
        Assert.Contains("\"features\": [\"raw-sectors\", \"subcode\", \"block-crc\"]", expected, StringComparison.Ordinal);
        Assert.Equal(PropsText.Replace(Pin.Commit, commit, StringComparison.Ordinal).Replace(Pin.UpstreamVersion, "bbbbbbb", StringComparison.Ordinal),
            AuthorityText.RewriteProps(PropsText, commit, "bbbbbbb").Value);
    }

    /// <summary>A duplicated identity key is ambiguous and fails instead of rewriting the first match.</summary>
    [Fact]
    public void DuplicatedIdentityFails()
    {
        string pin = PinText.Replace("\"features\"", $"\"mirror\": {{\"commit\": \"{Pin.Commit}\"}},\n  \"features\"", StringComparison.Ordinal);
        Assert.Contains("exactly one \"commit\"", AuthorityText.RewritePin(pin, Pin.Commit, Pin.UpstreamVersion, Pin.Headers).Failure.Message, StringComparison.Ordinal);
        string props = PropsText.Replace("</Project>", "  <PropertyGroup>\n    <LibchdrUpstreamCommit>x</LibchdrUpstreamCommit>\n  </PropertyGroup>\n</Project>", StringComparison.Ordinal);
        Assert.Contains("exactly one LibchdrUpstreamCommit", AuthorityText.RewriteProps(props, Pin.Commit, Pin.UpstreamVersion).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A match in the wrong place is caught by re-parsing rather than trusted.</summary>
    [Fact]
    public void MisplacedIdentityFails()
    {
        string pin = PinText.Replace("\"commit\"", "\"retired\"", StringComparison.Ordinal)
            .Replace("\"features\"", $"\"mirror\": {{\"commit\": \"{Pin.Commit}\"}},\n  \"features\"", StringComparison.Ordinal);
        Assert.Contains("more than the upstream identity", AuthorityText.RewritePin(pin, Pin.Commit, Pin.UpstreamVersion, Pin.Headers).Failure.Message, StringComparison.Ordinal);
        string props = PropsText.Replace($"    <LibchdrUpstreamCommit>{Pin.Commit}</LibchdrUpstreamCommit>\n", "", StringComparison.Ordinal)
            .Replace("  <ItemGroup>", $"  <ItemGroup>\n    <LibchdrUpstreamCommit>{Pin.Commit}</LibchdrUpstreamCommit>", StringComparison.Ordinal);
        Assert.Contains("exactly once", AuthorityText.RewriteProps(props, Pin.Commit, Pin.UpstreamVersion).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Identity values that would need escaping in JSON, XML or C# are refused.</summary>
    [Theory]
    [InlineData("v1\"injected")]
    [InlineData("v1<Version>2</Version>")]
    [InlineData("v1 2")]
    [InlineData("-v1")]
    [InlineData("")]
    public void UnsafeUpstreamVersionIsRejected(string version)
    {
        Assert.Contains("Unexpected upstream identity value", AuthorityText.RewritePin(PinText, Pin.Commit, version, Pin.Headers).Failure.Message, StringComparison.Ordinal);
        Assert.Contains("Unexpected upstream identity value", AuthorityText.RewriteProps(PropsText, Pin.Commit, version).Failure.Message, StringComparison.Ordinal);
    }
}
