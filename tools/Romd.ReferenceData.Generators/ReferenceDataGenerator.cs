using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Romd.ReferenceData.CodeGen;

namespace Romd.ReferenceData.Generators;

[Generator]
public sealed class ReferenceDataGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor Invalid = new("ROMDREF001", "Invalid reference catalog", "{0}", "ReferenceData", DiagnosticSeverity.Error, true);
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var inputs = context.AdditionalTextsProvider
            .Where(file => file.Path.EndsWith(".json", StringComparison.Ordinal))
            .Select((file, ct) => new KeyValuePair<string,string>(Path.GetFileNameWithoutExtension(file.Path), file.GetText(ct)?.ToString() ?? ""))
            .Collect();
        context.RegisterSourceOutput(inputs, (output, files) =>
        {
            try
            {
                var catalog = new Catalog(files.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
                output.AddSource("PlatformIds.g.cs", catalog.CSharpPlatforms());
                output.AddSource("RatingBoardCatalog.g.cs", catalog.CSharpRatings());
            }
            catch (Exception error) when (error is ArgumentException or Newtonsoft.Json.JsonException)
            {
                output.ReportDiagnostic(Diagnostic.Create(Invalid, Location.None, error.Message));
            }
        });
    }
}
