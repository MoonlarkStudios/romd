using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using Romd.ReferenceData.CodeGen;
using Romd.ReferenceData.Generators;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class ReferenceGeneratorTests
{
    private static Dictionary<string, string> Inputs() => Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "reference-data"), "*.json")
        .ToDictionary(p => Path.GetFileNameWithoutExtension(p), File.ReadAllText);

    [Fact]
    public void Generator_ValidCatalog_ProducesDeterministicCompilableCode()
    {
        var inputs = Inputs();
        var files = inputs.Select(p => (AdditionalText)new TextFile(p.Key + ".json", p.Value)).ToArray();
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new ReferenceDataGenerator().AsSourceGenerator()], additionalTexts: files);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("GeneratedCatalog", [CSharpSyntaxTree.ParseText("""
            namespace Romd.Domain.Catalog.Ratings {
              public enum RatingDesignation { Rated, RatingPending, RefusedClassification }
            }
            """)], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        diagnostics.ShouldBeEmpty();
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        var first = driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()).ToArray();
        driver = driver.RunGenerators(compilation);
        driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()).ShouldBe(first);
        first.ShouldContain(s => s.Contains("public const string Snes = \"snes\""));
    }

    [Theory]
    [InlineData("reserved-local-key")]
    [InlineData("invalid-retirement")]
    [InlineData("duplicate-symbol")]
    [InlineData("unknown-manufacturer")]
    [InlineData("duplicate-board-value")]
    [InlineData("invalid-age")]
    [InlineData("invalid-icon-path")]
    [InlineData("missing-compact-label")]
    public void Catalog_InvalidDefinitions_FailBeforeEmission(string mutation)
    {
        var inputs = Inputs();
        var systems = JObject.Parse(inputs["systems"]);
        var ratings = JObject.Parse(inputs["ratings"]);
        if (mutation == "reserved-local-key") systems["local-console"] = systems["snes"]!.DeepClone();
        if (mutation == "invalid-retirement") systems["snes"]!["retired"] = "yes";
        if (mutation == "duplicate-symbol") systems["snes"]!["symbol"] = systems["nes"]!["symbol"]!.DeepClone();
        if (mutation == "unknown-manufacturer") systems["snes"]!["manufacturerIds"] = new JArray("missing");
        if (mutation == "duplicate-board-value") ratings["boards"]![1]!["value"] = 0;
        if (mutation == "invalid-icon-path") systems["snes"]!["iconPath"] = "platforms/../../secret.png";
        if (mutation == "missing-compact-label") systems["snes"]!["compactLabel"] = "";
        if (mutation == "invalid-age") ratings["ratings"]![0]!["minimumAge"] = null;
        inputs["systems"] = systems.ToString(); inputs["ratings"] = ratings.ToString();
        Should.Throw<ArgumentException>(() => new Romd.ReferenceData.CodeGen.Catalog(inputs));
    }

    [Fact]
    public void Generator_MissingInput_ReportsActionableDiagnostic()
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ReferenceDataGenerator());
        driver = driver.RunGenerators(CSharpCompilation.Create("Missing"));
        var diagnostic = driver.GetRunResult().Diagnostics.Single();
        diagnostic.Id.ShouldBe("ROMDREF001");
        diagnostic.GetMessage().ShouldContain("Missing catalog");
    }

    private sealed class TextFile(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }
}
