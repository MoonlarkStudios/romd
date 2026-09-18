using System.Text.Json;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class NumericOpenApiBoundaryTests
{
    private const string CanonicalIntegerPattern = "^-?(?:0|[1-9]\\d*)$";
    private const string CanonicalUnsignedIntegerPattern = "^(?:0|[1-9]\\d*)$";
    private static readonly string[] SchemaCompositionKeywords = ["oneOf", "anyOf", "allOf"];
    private static readonly IReadOnlyDictionary<string, ReviewedIdentityException> ReviewedIdentityExceptions =
        new Dictionary<string, ReviewedIdentityException>(StringComparer.Ordinal)
        {
            ["admin:components.schemas.TitleEnrichmentStateResponse.externalIds"] =
                new(
                    "This container holds evidence records rather than persisted identity; its numeric " +
                    "matchConfidence sibling creates the broad-scan false positive.",
                    "externalId")
        };

    [Theory]
    [InlineData("web/schemas/admin-v1.json", 184)]
    [InlineData("web/schemas/consumer-v1.json", 21)]
    public void CommittedOpenApi_NumericStringUnions_AreEliminated(
        string relativePath,
        int currentFailFirstCount)
    {
        using var document = ReadSchema(relativePath);

        int count = CountNumericStringUnions(document.RootElement, Schemas(document));

        count.ShouldBe(
            0,
            $"Fail-first baseline for {relativePath} was {currentFailFirstCount} numeric/string unions");
    }

    [Fact]
    public void AdminOpenApi_RepresentativeNumericPolicies_AreExact()
    {
        using var document = ReadSchema("web/schemas/admin-v1.json");
        JsonElement schemas = Schemas(document);

        AssertNumericSchema(Property(schemas, "BatchDeleteResponse", "deletedCount"), "integer", "int32");
        AssertNullableNumericSchema(Property(schemas, "TitleDetail", "players"), "integer", "int32");
        AssertNullableNumericSchema(Property(schemas, "TitleDetail", "rating"), "number", "double");
        AssertNumericSchema(Property(schemas, "LibrarySummary", "coverageHealthPercent"), "number", "double");
        AssertNumericSchema(Property(schemas, "WedgedReplaceDatJobDto", "stalledForSeconds"), "integer", "int64");
        AssertCanonicalUnsignedIntegerString(Resolve(Property(schemas, "Rom", "size"), schemas));
        AssertCanonicalIntegerString(Property(schemas, "QueueBacklogDiagnosticDto", "enqueuedCount"));
    }

    [Fact]
    public void ConsumerOpenApi_RepresentativeNumericPolicies_AreExact()
    {
        using var document = ReadSchema("web/schemas/consumer-v1.json");
        JsonElement schemas = Schemas(document);

        AssertNumericSchema(Property(schemas, "ConsumerPlatformSummaryDto", "titleCount"), "integer", "int32");
        AssertNullableNumericSchema(Property(schemas, "ConsumerTitleCardDto", "rating"), "number", "double");
        AssertCanonicalUnsignedIntegerString(Resolve(Property(schemas, "ConsumerReleaseDto", "sizeBytes"), schemas));
    }

    [Fact]
    public void AdminOpenApi_SqidQueryParameters_ArePlainStrings()
    {
        using var document = ReadSchema("web/schemas/admin-v1.json");

        AssertStringQueryParameter(document, "/api/upload", "post", "systemKey");
        AssertStringQueryParameter(document, "/api/upload/dat", "post", "systemKey");
        AssertStringQueryParameter(document, "/api/dats/{datId}/replace", "put", "systemKey");
    }

    [Fact]
    public void AdminOpenApi_BatchDeletePublishesCollectionInvariants()
    {
        using var document = ReadSchema("web/schemas/admin-v1.json");
        JsonElement romIds = Property(Schemas(document), "BatchDeleteRomsRequest", "romIds");

        romIds.GetProperty("minItems").GetInt32().ShouldBe(1);
        romIds.GetProperty("maxItems").GetInt32().ShouldBe(1000);
        romIds.GetProperty("uniqueItems").GetBoolean().ShouldBeTrue();
    }

    [Theory]
    [InlineData("web/schemas/admin-v1.json")]
    [InlineData("web/schemas/consumer-v1.json")]
    public void CommittedOpenApi_PublicIdentitySchemas_HaveNoNumericLeaf(string relativePath)
    {
        using var document = ReadSchema(relativePath);
        string surface = relativePath.Contains("admin", StringComparison.Ordinal) ? "admin" : "consumer";
        IReadOnlyList<string> candidates = FindNumericIdentityCandidates(document, surface);

        FindPublicIdentityViolations(document, surface).ShouldBeEmpty(
            "Public identity contracts, including Ids collections, must be opaque strings");
        ReviewedIdentityExceptions.Keys
            .Where(path => path.StartsWith(surface + ":", StringComparison.Ordinal))
            .Except(candidates, StringComparer.Ordinal)
            .ShouldBeEmpty("Remove reviewed Id-suffix exceptions once their schema no longer has numeric siblings");
        ReviewedIdentityExceptions.Values.ShouldAllBe(exception => !string.IsNullOrWhiteSpace(exception.Reason));
    }

    [Fact]
    public void ReviewedIdentityException_DoesNotMaskANumericExternalIdLeaf()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "components": {
                "schemas": {
                  "TitleEnrichmentStateResponse": {
                    "type": "object",
                    "properties": {
                      "externalIds": {
                        "type": "array",
                        "items": {
                          "type": "object",
                          "properties": {
                            "externalId": { "type": "integer" },
                            "matchConfidence": { "type": "number" }
                          }
                        }
                      }
                    }
                  }
                }
              },
              "paths": {}
            }
            """);

        FindPublicIdentityViolations(document, "admin")
            .ShouldContain("admin:components.schemas.TitleEnrichmentStateResponse.externalIds.externalId");
    }

    [Fact]
    public void ReviewedIdentityException_ValidatesEveryReferencedAndComposedLeaf()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "components": {
                "schemas": {
                  "ExternalIdStringRecord": {
                    "type": "object",
                    "properties": {
                      "externalId": { "type": "string" }
                    }
                  },
                  "TitleEnrichmentStateResponse": {
                    "type": "object",
                    "properties": {
                      "externalIds": {
                        "type": "array",
                        "items": {
                          "$ref": "#/components/schemas/ExternalIdStringRecord",
                          "oneOf": [
                            {
                              "type": "object",
                              "properties": {
                                "externalId": { "type": "string" }
                              }
                            },
                            {
                              "type": "object",
                              "properties": {
                                "externalId": { "type": "integer" }
                              }
                            }
                          ]
                        }
                      }
                    }
                  }
                }
              },
              "paths": {}
            }
            """);

        FindPublicIdentityViolations(document, "admin")
            .ShouldContain("admin:components.schemas.TitleEnrichmentStateResponse.externalIds.externalId");
    }

    [Fact]
    public void ReviewedIdentityException_RejectsAnAllOfNumericNamedLeaf()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "components": {
                "schemas": {
                  "TitleEnrichmentStateResponse": {
                    "type": "object",
                    "properties": {
                      "externalIds": {
                        "type": "array",
                        "items": {
                          "type": "object",
                          "properties": {
                            "externalId": {
                              "type": "string",
                              "allOf": [
                                { "type": "integer" }
                              ]
                            },
                            "matchConfidence": { "type": "number" }
                          }
                        }
                      }
                    }
                  }
                }
              },
              "paths": {}
            }
            """);

        FindPublicIdentityViolations(document, "admin")
            .ShouldContain("admin:components.schemas.TitleEnrichmentStateResponse.externalIds.externalId");
    }

    [Theory]
    [InlineData("id")]
    [InlineData("ids")]
    [InlineData("systemKey")]
    [InlineData("romIds")]
    public void IdentityNamePolicy_CoversTopLevelAndSuffixedJsonNames(string name)
    {
        IsIdentityName(name).ShouldBeTrue();
    }

    [Theory]
    [InlineData("oneOf")]
    [InlineData("anyOf")]
    [InlineData("allOf")]
    public void NumericUnionPolicy_CatchesComposedStringAndIntegerShapes(string composition)
    {
        string json = """{"components":{"schemas":{}},"COMPOSITION":[{"type":"string"},{"type":"integer"}]}"""
            .Replace("COMPOSITION", composition, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);

        CountNumericStringUnions(document.RootElement, Schemas(document)).ShouldBe(1);
    }

    [Theory]
    [InlineData("oneOf")]
    [InlineData("anyOf")]
    [InlineData("allOf")]
    public void NumericUnionPolicy_ComposesReferenceAndSiblingShapes(string composition)
    {
        string json =
            """{"components":{"schemas":{"Text":{"type":"string"}}},"subject":{"$ref":"#/components/schemas/Text","COMPOSITION":[{"type":"integer"}]}}"""
                .Replace("COMPOSITION", composition, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);

        CountNumericStringUnions(
            document.RootElement.GetProperty("subject"),
            Schemas(document)).ShouldBe(1);
    }

    [Theory]
    [InlineData("oneOf")]
    [InlineData("anyOf")]
    [InlineData("allOf")]
    public void IdentityPolicy_ComposesReferenceAndSiblingShapes(string composition)
    {
        string json =
            """{"components":{"schemas":{"Text":{"type":"string"}}},"subject":{"$ref":"#/components/schemas/Text","COMPOSITION":[{"type":"integer"}]}}"""
                .Replace("COMPOSITION", composition, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);

        HasNumericLeaf(
            document.RootElement.GetProperty("subject"),
            Schemas(document),
            []).ShouldBeTrue();
    }

    private static IReadOnlyList<string> FindPublicIdentityViolations(JsonDocument document, string surface)
    {
        JsonElement schemas = Schemas(document);
        IReadOnlyList<string> candidates = FindNumericIdentityCandidates(document, surface);
        var violations = candidates
            .Where(path => !ReviewedIdentityExceptions.ContainsKey(path))
            .ToList();

        foreach ((string path, ReviewedIdentityException exception) in ReviewedIdentityExceptions)
        {
            if (!path.StartsWith(surface + ":", StringComparison.Ordinal) ||
                !candidates.Contains(path))
                continue;

            JsonElement container = ResolveIdentityCandidate(document, path);
            IReadOnlyList<IdentityLeaf> leaves = FindIdentityLeaves(
                container,
                schemas,
                exception.OpaqueLeafName);
            if (leaves.Count == 0 || leaves.Any(leaf => !IsOpaqueString(leaf.Schema, schemas)))
                violations.Add($"{path}.{exception.OpaqueLeafName}");
        }

        return violations;
    }

    private static IReadOnlyList<string> FindNumericIdentityCandidates(JsonDocument document, string surface)
    {
        JsonElement schemas = Schemas(document);
        var candidates = new List<string>();

        foreach (JsonProperty component in schemas.EnumerateObject())
        {
            if (!component.Value.TryGetProperty("properties", out JsonElement properties))
                continue;

            foreach (JsonProperty property in properties.EnumerateObject())
            {
                if (IsIdentityName(property.Name) && HasNumericLeaf(property.Value, schemas, []))
                    candidates.Add($"{surface}:components.schemas.{component.Name}.{property.Name}");
            }
        }

        if (!document.RootElement.TryGetProperty("paths", out JsonElement paths))
            return candidates;

        foreach (JsonProperty path in paths.EnumerateObject())
        {
            foreach (JsonProperty operation in path.Value.EnumerateObject())
            {
                if (!operation.Value.TryGetProperty("parameters", out JsonElement parameters))
                    continue;

                foreach (JsonElement parameter in parameters.EnumerateArray())
                {
                    string? name = parameter.GetProperty("name").GetString();
                    if (name is not null && IsIdentityName(name) &&
                        HasNumericLeaf(parameter.GetProperty("schema"), schemas, []))
                    {
                        candidates.Add($"{surface}:paths.{path.Name}.{operation.Name}.parameters.{name}");
                    }
                }
            }
        }

        return candidates;
    }

    private static JsonElement ResolveIdentityCandidate(JsonDocument document, string candidatePath)
    {
        string[] segments = candidatePath.Split('.');
        return Property(Schemas(document), segments[^2], segments[^1]);
    }

    private static IReadOnlyList<IdentityLeaf> FindIdentityLeaves(
        JsonElement schema,
        JsonElement schemas,
        string opaqueLeafName,
        HashSet<string>? visitedReferences = null)
    {
        visitedReferences ??= [];
        var leaves = new List<IdentityLeaf>();
        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            string name = reference.GetString()!.Split('/').Last();
            if (visitedReferences.Add(name))
            {
                leaves.AddRange(FindIdentityLeaves(
                    schemas.GetProperty(name),
                    schemas,
                    opaqueLeafName,
                    new HashSet<string>(visitedReferences)));
            }
        }

        if (schema.TryGetProperty("items", out JsonElement items))
        {
            leaves.AddRange(FindIdentityLeaves(
                items,
                schemas,
                opaqueLeafName,
                new HashSet<string>(visitedReferences)));
        }

        if (schema.TryGetProperty("properties", out JsonElement properties))
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                if (property.Name == opaqueLeafName)
                {
                    leaves.Add(new IdentityLeaf(property.Value));
                    continue;
                }

                leaves.AddRange(FindIdentityLeaves(
                        property.Value,
                        schemas,
                        opaqueLeafName,
                        new HashSet<string>(visitedReferences)));
            }
        }

        foreach (string composition in SchemaCompositionKeywords)
        {
            if (!schema.TryGetProperty(composition, out JsonElement members))
                continue;

            foreach (JsonElement member in members.EnumerateArray())
            {
                leaves.AddRange(FindIdentityLeaves(
                        member,
                        schemas,
                        opaqueLeafName,
                        new HashSet<string>(visitedReferences)));
            }
        }

        return leaves;
    }

    private static bool IsOpaqueString(JsonElement schema, JsonElement schemas)
    {
        HashSet<string> types = CollectWireTypes(schema, schemas, []);
        return types.Contains("string") && types.All(type => type is "string" or "null");
    }

    private static void AssertStringQueryParameter(
        JsonDocument document,
        string path,
        string method,
        string parameterName)
    {
        JsonElement parameter = document.RootElement
            .GetProperty("paths")
            .GetProperty(path)
            .GetProperty(method)
            .GetProperty("parameters")
            .EnumerateArray()
            .Single(candidate => candidate.GetProperty("name").GetString() == parameterName);
        JsonElement schema = Resolve(parameter.GetProperty("schema"), Schemas(document));

        schema.GetProperty("type").GetString().ShouldBe("string");
        schema.TryGetProperty("properties", out _).ShouldBeFalse();
    }

    private static bool HasNumericLeaf(
        JsonElement schema,
        JsonElement schemas,
        HashSet<string> visitedReferences)
    {
        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            string name = reference.GetString()!.Split('/').Last();
            if (visitedReferences.Add(name) &&
                HasNumericLeaf(schemas.GetProperty(name), schemas, new HashSet<string>(visitedReferences)))
                return true;
        }

        if (schema.TryGetProperty("type", out JsonElement type))
        {
            if (type.ValueKind == JsonValueKind.String && type.GetString() is "integer" or "number")
                return true;
            if (type.ValueKind == JsonValueKind.Array && type.EnumerateArray()
                    .Any(item => item.GetString() is "integer" or "number"))
                return true;
        }

        if (schema.TryGetProperty("items", out JsonElement items) &&
            HasNumericLeaf(items, schemas, new HashSet<string>(visitedReferences)))
            return true;

        if (schema.TryGetProperty("properties", out JsonElement properties) && properties
                .EnumerateObject()
                .Any(property => HasNumericLeaf(
                    property.Value,
                    schemas,
                    new HashSet<string>(visitedReferences))))
            return true;

        foreach (string composition in SchemaCompositionKeywords)
        {
            if (schema.TryGetProperty(composition, out JsonElement members) && members
                    .EnumerateArray()
                    .Any(member => HasNumericLeaf(
                        member,
                        schemas,
                        new HashSet<string>(visitedReferences))))
                return true;
        }

        return false;
    }

    private static bool IsIdentityName(string name) =>
        name.Equals("key", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Key", StringComparison.Ordinal) ||
        name.EndsWith("Keys", StringComparison.Ordinal) ||
        name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ids", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Id", StringComparison.Ordinal) ||
        name.EndsWith("Ids", StringComparison.Ordinal);

    private static int CountNumericStringUnions(JsonElement element, JsonElement schemas)
    {
        int count = 0;
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> wireTypes = CollectWireTypes(element, schemas, []);
            if (wireTypes.Contains("string") && (wireTypes.Contains("integer") || wireTypes.Contains("number")))
                count++;

            count += element.EnumerateObject().Sum(property => CountNumericStringUnions(property.Value, schemas));
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            count += element.EnumerateArray().Sum(item => CountNumericStringUnions(item, schemas));
        }

        return count;
    }

    private static HashSet<string> CollectWireTypes(
        JsonElement schema,
        JsonElement schemas,
        HashSet<string> visitedReferences)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return [];

        var types = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            string name = reference.GetString()!.Split('/').Last();
            if (visitedReferences.Add(name))
            {
                types.UnionWith(CollectWireTypes(
                    schemas.GetProperty(name),
                    schemas,
                    new HashSet<string>(visitedReferences)));
            }
        }

        if (schema.TryGetProperty("type", out JsonElement type))
        {
            if (type.ValueKind == JsonValueKind.String)
                types.Add(type.GetString()!);
            else if (type.ValueKind == JsonValueKind.Array)
                types.UnionWith(type.EnumerateArray().Select(member => member.GetString()!));
        }

        foreach (string composition in SchemaCompositionKeywords)
        {
            if (!schema.TryGetProperty(composition, out JsonElement members))
                continue;

            foreach (JsonElement member in members.EnumerateArray())
                types.UnionWith(CollectWireTypes(member, schemas, new HashSet<string>(visitedReferences)));
        }

        return types;
    }

    private static void AssertNumericSchema(JsonElement schema, string type, string format)
    {
        schema.GetProperty("type").GetString().ShouldBe(type);
        schema.GetProperty("format").GetString().ShouldBe(format);
        schema.TryGetProperty("pattern", out _).ShouldBeFalse();
    }

    private static void AssertNullableNumericSchema(JsonElement schema, string numericType, string format)
    {
        schema.GetProperty("type").EnumerateArray().Select(item => item.GetString())
            .ShouldBe(["null", numericType]);
        schema.GetProperty("format").GetString().ShouldBe(format);
        schema.TryGetProperty("pattern", out _).ShouldBeFalse();
    }

    private static void AssertCanonicalIntegerString(JsonElement schema)
    {
        schema.GetProperty("type").GetString().ShouldBe("string");
        schema.GetProperty("pattern").GetString().ShouldBe(CanonicalIntegerPattern);
    }

    private static void AssertCanonicalUnsignedIntegerString(JsonElement schema)
    {
        schema.GetProperty("type").GetString().ShouldBe("string");
        schema.GetProperty("pattern").GetString().ShouldBe(CanonicalUnsignedIntegerPattern);
    }

    private static JsonElement Property(JsonElement schemas, string schemaName, string propertyName) =>
        schemas.GetProperty(schemaName).GetProperty("properties").GetProperty(propertyName);

    private static JsonElement Schemas(JsonDocument document) =>
        document.RootElement.GetProperty("components").GetProperty("schemas");

    private static JsonElement Resolve(JsonElement schema, JsonElement schemas) =>
        schema.TryGetProperty("$ref", out JsonElement reference)
            ? schemas.GetProperty(reference.GetString()!.Split('/').Last())
            : schema;

    private static JsonDocument ReadSchema(string relativePath) =>
        JsonDocument.Parse(File.ReadAllText(FindRepositoryFile(relativePath)));

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }

    private sealed record ReviewedIdentityException(string Reason, string OpaqueLeafName);
    private sealed record IdentityLeaf(JsonElement Schema);
}
