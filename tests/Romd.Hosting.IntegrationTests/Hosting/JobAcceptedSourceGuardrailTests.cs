using System.Text.RegularExpressions;
using Shouldly;
using Xunit;
using static Romd.Hosting.IntegrationTests.Infrastructure.RepositoryPaths;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>One producer for accepted work, with no legacy exceptions.</summary>
public sealed class JobAcceptedSourceGuardrailTests
{
    private static readonly Regex Forbidden = new(@"\b(?:Results|TypedResults)\s*\.\s*Accepted\s*\(|\bStatus202Accepted\b", RegexOptions.CultureInvariant);

    [Fact]
    public void EndpointCode_AcceptsJobsOnlyThroughSharedHelper()
    {
        var violations = Directory.GetFiles(FindRepositoryDirectory("src/Romd.Hosting/Endpoints"), "*.cs", SearchOption.AllDirectories)
            .Where(IsRepositorySourceFile)
            .Where(path => ToRepositoryRelativePath(path) != "src/Romd.Hosting/Endpoints/JobAccepted.cs")
            .SelectMany(path => Scan(ToRepositoryRelativePath(path), File.ReadAllText(path)))
            .ToArray();
        violations.ShouldBeEmpty(string.Join("\n", violations));
    }

    [Fact]
    public void Scan_IgnoresProseAndFindsAllAcceptedForms()
    {
        Scan("other.cs", "// Results.Accepted()\nvar text = \"Status202Accepted\";\nResults.Accepted(); TypedResults.Accepted(); StatusCodes.Status202Accepted;")
            .ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("Results.Accepted();")]
    [InlineData("TypedResults.Accepted();")]
    [InlineData("StatusCodes.Status202Accepted;")]
    public void Scan_EachAcceptedFormIsRejectedIndependently(string source) => Scan("other.cs", source).ShouldHaveSingleItem();

    private static IReadOnlyList<string> Scan(string file, string source) =>
        EnvelopeSourceScan.Blank(source, keepStrings: false).Split('\n')
            .Select((line, index) => (line, index))
            .Where(item => Forbidden.IsMatch(item.line))
            .Select(item => $"{file}:{item.index + 1}: accepted work must use JobAccepted")
            .ToArray();
}
