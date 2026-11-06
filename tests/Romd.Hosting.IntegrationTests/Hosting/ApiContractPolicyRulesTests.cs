using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class ApiContractPolicyRulesTests
{
    [Fact]
    public void R6_AcceptanceWithETag_IsAViolation()
    {
        using var document = JsonDocument.Parse("""
            {"paths":{"/api/work":{"post":{"responses":{"202":{
              "headers":{"Location":{"schema":{"type":"string"}},"ETag":{"schema":{"type":"string"}}},
              "content":{"application/json":{"schema":{"type":"object","properties":{"jobId":{"type":"string"}}}}}
            }}}}}}
            """);
        ApiContractPolicyRules.All.Single(rule => rule.Id == "R6")
            .FindViolations(ApiOperation.FromDocument("admin", document)).ShouldBe(["admin POST /api/work"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R7_RequiredIfMatch_RequiresMalformedHeaderResponse(bool declaresMalformedHeader)
    {
        using var document = JsonDocument.Parse("""
            {"paths":{"/api/things/{thingId}":{"patch":{
              "parameters":[{"name":"If-Match","in":"header","required":true,"schema":{"type":"string"}}],
              "responses":{"200":{},"412":{},"428":{}}
            }}}}
            """);
        var root = JsonNode.Parse(document.RootElement.GetRawText()).ShouldNotBeNull();
        if (declaresMalformedHeader)
        {
            var responses = root["paths"].ShouldNotBeNull()["/api/things/{thingId}"].ShouldNotBeNull()
                ["patch"].ShouldNotBeNull()["responses"].ShouldNotBeNull();
            responses["400"] = new JsonObject();
        }
        using var candidate = JsonDocument.Parse(root.ToJsonString());

        Rule("R7").FindViolations(ApiOperation.FromDocument("admin", candidate))
            .ShouldBe(declaresMalformedHeader ? [] : ["admin PATCH /api/things/{thingId}"]);
    }

    private const string SyntheticDocument =
        """
        {
          "paths": {
            "/media/{mediaId}": {
              "delete": {
                "requestBody": { "content": { "application/json": { "schema": { "type": "object" } } } },
                "responses": { "204": {} }
              }
            },
            "/mediakit": { "get": { "responses": { "200": {} } } },
            "/artwork/{assetId}/{variantName}": { "get": { "responses": { "200": {} } } },
            "/artwork/x": { "get": { "responses": { "200": {} } } },
            "/api/admin/things/{thingId}": {
              "get": {
                "operationId": "GetThing",
                "parameters": [ { "name": "thingId", "in": "path", "required": true, "schema": { "type": "string" } } ],
                "responses": { "200": {}, "304": {}, "401": {}, "403": {} }
              }
            },
            "/api/platform_things": {
              "get": {
                "operationId": "ListThingsByKind",
                "parameters": [
                  { "name": "after", "in": "query", "schema": { "type": "string" } },
                  { "name": "limit", "in": "query", "schema": { "type": "integer" } },
                  { "name": "search", "in": "query", "schema": { "type": "string" } },
                  { "name": "platformId", "in": "query", "schema": { "type": "string" } }
                ],
                "responses": {
                  "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/PageOfThing" } } } },
                  "400": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/Thing" } } } }
                }
              }
            },
            "/api/things": {
              "get": {
                "operationId": "ListPlatformThings",
                "parameters": [ { "name": "limit", "in": "query", "schema": { "type": "integer", "maximum": 100 } } ],
                "responses": {
                  "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ThingFeed" } } } },
                  "400": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } }
                }
              },
              "post": {
                "operationId": "CreateThing",
                "requestBody": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/CreateThingRequest" } } } },
                "responses": {
                  "202": {
                    "headers": { "Location": { "schema": { "type": "string" } } },
                    "content": { "application/json": { "schema": { "$ref": "#/components/schemas/JobReference" } } }
                  },
                  "409": {}
                }
              },
              "delete": {
                "operationId": "DeleteThings",
                "requestBody": {
                  "content": {
                    "application/json": {
                      "schema": { "type": "object", "properties": { "expectedRevision": { "type": "string" } } }
                    }
                  }
                },
                "responses": { "204": {} }
              }
            },
            "/api/things/batch-run": {
              "post": {
                "operationId": "RunThings",
                "responses": {
                  "202": {
                    "headers": { "location": { "schema": { "type": "string" } } },
                    "content": {
                      "application/json": {
                        "schema": { "type": "object", "properties": { "statusUrl": { "type": "string" } } }
                      }
                    }
                  },
                  "503": {}
                }
              }
            },
            "/api/thing-copies": {
              "post": {
                "operationId": "CreateThingCopy",
                "responses": { "201": { "content": { "application/json": { "schema": { "type": "object" } } } } }
              }
            },
            "/api/thing-links": {
              "post": {
                "operationId": "CreateThingLink",
                "responses": { "201": { "headers": { "location": { "schema": { "type": "string" } } } } }
              }
            },
            "/api/things/legacy": {
              "get": {
                "operationId": "ListLegacyThings",
                "parameters": [
                  { "name": "offset", "in": "query", "schema": { "type": "integer" } },
                  { "name": "page", "in": "query", "schema": { "type": "integer" } },
                  { "name": "pageSize", "in": "query", "schema": { "type": "integer" } }
                ],
                "responses": {
                  "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/LegacyEnvelope" } } } }
                }
              }
            },
            "/api/things/legacy-rows": {
              "get": {
                "operationId": "ListLegacyThingRows",
                "responses": {
                  "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/LegacyRows" } } } }
                }
              }
            },
            "/api/things/{thingId}/rebuild": {
              "post": {
                "operationId": "RebuildThing",
                "responses": {
                  "202": {
                    "headers": { "location": { "schema": { "type": "string" } } },
                    "content": { "application/json": { "schema": { "$ref": "#/components/schemas/JobReference" } } }
                  },
                  "404": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } },
                  "500": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } }
                }
              }
            },
            "/api/things/{thingId}/state": {
              "parameters": [ { "name": "thingId", "in": "path", "required": true, "schema": { "type": "string" } } ],
              "put": {
                "operationId": "ReplaceThingState",
                "parameters": [ { "name": "If-Match", "in": "header", "schema": { "type": "string" } } ],
                "responses": {
                  "202": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/JobReference" } } } },
                  "404": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } },
                  "412": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } },
                  "428": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } }
                }
              },
              "patch": {
                "operationId": "UpdateThingState",
                "parameters": [
                  { "name": "if-match", "in": "header", "required": true, "schema": { "type": "string" } },
                  { "name": "X-Platform-Key", "in": "header", "schema": { "type": "string" } }
                ],
                "responses": {
                  "200": {},
                  "400": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } },
                  "404": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } },
                  "412": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } },
                  "428": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } }
                }
              },
              "delete": {
                "operationId": "DeleteThingState",
                "parameters": [ { "name": "If-Match", "in": "header", "required": true, "schema": { "type": "string" } } ],
                "responses": {
                  "204": {},
                  "404": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } },
                  "412": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } }
                }
              }
            },
            "/api/things/{thingId}/label": {
              "put": {
                "operationId": "ReplaceThingLabel",
                "responses": {
                  "200": {},
                  "404": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } },
                  "412": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } },
                  "428": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } }
                }
              }
            },
            "/api/things/{thingId}/note": {
              "patch": {
                "operationId": "UpdateThingNote",
                "parameters": [
                  { "name": "If-Match", "in": "header", "schema": { "type": "string" } },
                  { "name": "expectedRevision", "in": "query", "schema": { "type": "string" } },
                  { "name": "X-Snapshot-Revision", "in": "header", "schema": { "type": "string" } }
                ],
                "responses": {
                  "200": {},
                  "404": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } }
                }
              }
            },
            "/api/things/{thingId}/rename": {
              "post": {
                "operationId": "RenameThing",
                "requestBody": { "content": { "application/json": { "schema": { "type": "object" } } } },
                "responses": {
                  "200": {},
                  "400": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } },
                  "404": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } }
                }
              }
            },
            "/api/things/{thingId}/retag": {
              "post": {
                "operationId": "RetagThing",
                "requestBody": { "content": { "application/json": { "schema": { "type": "object" } } } },
                "responses": {
                  "200": {},
                  "400": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } },
                  "404": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } }
                }
              }
            },
            "/api/things/{thingId}/tag": {
              "delete": {
                "operationId": "DeleteThingTag",
                "responses": {
                  "204": {},
                  "404": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } },
                  "428": { "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } }
                }
              }
            }
          },
          "components": {
            "schemas": {
              "ProblemDetails": { "type": "object" },
              "Thing": { "type": "object", "properties": { "name": { "type": "string" } } },
              "JobReference": { "type": "object", "properties": { "jobId": { "type": "string" } } },
              "CreateThingRequest": {
                "type": "object",
                "properties": {
                  "revision": { "type": "string" },
                  "offset": { "type": "integer" },
                  "parts": { "type": "array", "items": { "allOf": [ { "$ref": "#/components/schemas/ThingPart" } ] } },
                  "labels": {
                    "type": "object",
                    "additionalProperties": { "type": "object", "properties": { "labelRevision": { "type": "string" } } }
                  },
                  "choice": { "oneOf": [ { "type": "null" }, { "$ref": "#/components/schemas/ThingChoice" } ] },
                  "either": { "anyOf": [ { "type": "object", "properties": { "eitherRevision": { "type": "string" } } } ] },
                  "pair": {
                    "type": "array",
                    "prefixItems": [ { "type": "object", "properties": { "pairRevision": { "type": "string" } } } ]
                  },
                  "exclusion": { "not": { "type": "object", "properties": { "notRevision": { "type": "string" } } } }
                }
              },
              "ThingPart": {
                "type": "object",
                "properties": {
                  "expectedRevision": { "type": "string" },
                  "priorRevisions": { "type": "array", "items": { "type": "string" } },
                  "pageSize": { "type": "integer" },
                  "inline": { "type": "object", "properties": { "selectionRevision": { "type": "string" } } }
                }
              },
              "ThingChoice": { "type": "object", "properties": { "choiceRevision": { "type": "string" } } },
              "PageOfThing": {
                "type": "object",
                "properties": { "items": { "type": "array" }, "nextCursor": {}, "hasNextPage": {}, "totalCount": {} }
              },
              "ThingFeed": {
                "type": "object",
                "properties": {
                  "revision": { "type": "string" },
                  "items": { "type": "array" },
                  "hasMore": { "type": "boolean" },
                  "page": { "$ref": "#/components/schemas/PageOfGood" }
                }
              },
              "PageOfGood": {
                "type": "object",
                "properties": { "items": { "type": "array" }, "nextCursor": {}, "hasNextPage": {} }
              },
              "LegacyEnvelope": {
                "type": "object",
                "properties": { "paging": { "type": "object", "properties": { "pageSize": { "type": "integer" } } } }
              },
              "LegacyRows": {
                "type": "object",
                "properties": { "rows": { "type": "array", "items": { "$ref": "#/components/schemas/LegacyRow" } } }
              },
              "LegacyRow": { "type": "object", "properties": { "offset": { "type": "integer" } } }
            }
          }
        }
        """;

    private const string UnsupportedShapeDocument =
        """
        {
          "paths": {
            "/api/things/{thingId}": {
              "parameters": [ { "$ref": "#/components/parameters/ThingId" } ],
              "get": {
                "parameters": [
                  { "in": "query" },
                  { "name": "limit", "in": "query", "schema": { "$ref": "#/components/schemas/MissingLimit" } }
                ],
                "responses": {
                  "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Thing" } } } },
                  "404": { "$ref": "#/components/responses/NotFound" },
                  "default": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Missing" } } } }
                }
              },
              "post": {
                "responses": {
                  "202": {
                    "headers": { "Location": { "schema": { "type": "string" } } },
                    "content": { "application/json": { "schema": { "$ref": "#/components/schemas/MissingJob" } } }
                  }
                }
              }
            }
          },
          "components": {
            "schemas": {
              "Thing": { "type": "object", "properties": { "owner": { "$ref": "#/components/schemas/MissingOwner" } } }
            }
          }
        }
        """;

    [Theory]
    [InlineData("R1", new[] { "admin GET /api/admin/things/{thingId}" })]
    [InlineData("R2", new[]
    {
        "admin GET /api/platform_things", "admin GET /api/things", "admin PATCH /api/things/{thingId}/state"
    })]
    [InlineData("R3", new[] { "admin GET /api/platform_things" })]
    [InlineData("R4", new[] { "admin DELETE /api/things" })]
    [InlineData("R5", new[] { "admin PUT /api/things/{thingId}/state" })]
    [InlineData("R6", new[] { "admin POST /api/things/batch-run", "admin PUT /api/things/{thingId}/state" })]
    [InlineData("R7", new[]
    {
        "admin DELETE /api/things/{thingId}/state",
        "admin DELETE /api/things/{thingId}/tag",
        "admin PATCH /api/things/{thingId}/note",
        "admin PUT /api/things/{thingId}/label",
        "admin PUT /api/things/{thingId}/state"
    })]
    [InlineData("R8", new[]
    {
        "admin GET /api/platform_things 400",
        "admin GET /api/things 400",
        "admin POST /api/things 409",
        "admin POST /api/things/batch-run 503",
        "admin POST /api/things/{thingId}/retag 400"
    })]
    [InlineData("R9", new[] { "admin GET /api/admin/things/{thingId}" })]
    [InlineData("R10", new[]
    {
        "admin GET /api/platform_things ?after",
        "admin GET /api/platform_things ?limit",
        "admin GET /api/platform_things ?platformId",
        "admin GET /api/platform_things ?search"
    })]
    [InlineData("R11", new[]
    {
        "admin CreateThingRequest.either.eitherRevision",
        "admin CreateThingRequest.exclusion.notRevision",
        "admin CreateThingRequest.labels.labelRevision",
        "admin CreateThingRequest.pair.pairRevision",
        "admin CreateThingRequest.revision",
        "admin DELETE /api/things requestBody.expectedRevision",
        "admin PATCH /api/things/{thingId}/note ?expectedRevision",
        "admin PATCH /api/things/{thingId}/note header:X-Snapshot-Revision",
        "admin ThingChoice.choiceRevision",
        "admin ThingPart.expectedRevision",
        "admin ThingPart.inline.selectionRevision",
        "admin ThingPart.priorRevisions"
    })]
    [InlineData("R12", new[] { "admin PageOfThing", "admin ThingFeed" })]
    [InlineData("R13", new[]
    {
        "admin GET /api/things/legacy 200",
        "admin GET /api/things/legacy ?offset",
        "admin GET /api/things/legacy ?page",
        "admin GET /api/things/legacy ?pageSize",
        "admin GET /api/things/legacy-rows 200",
        "admin POST /api/things requestBody.offset",
        "admin POST /api/things requestBody.parts.pageSize"
    })]
    [InlineData("R14", new[]
    {
        "admin DELETE /api/things", "admin POST /api/things", "admin POST /api/things/{thingId}/retag"
    })]
    [InlineData("R15", new[] { "admin POST /api/thing-copies" })]
    public void FindViolations_SyntheticDocument_ReportsExactlyTheSeededViolations(string ruleId, string[] expected)
    {
        using var document = JsonDocument.Parse(SyntheticDocument);
        IReadOnlyList<ApiOperation> operations = ApiOperation.FromDocument("admin", document);

        IEnumerable<string> violations = Rule(ruleId).FindViolations(operations);

        violations.Distinct(StringComparer.Ordinal).ShouldBe(expected, ignoreOrder: true);
    }

    [Fact]
    public void FindRoutesOutsideApi_SyntheticDocument_ReportsOnlyNonProtocolRoutes()
    {
        using var document = JsonDocument.Parse(SyntheticDocument);

        ApiContractPolicyRules.FindRoutesOutsideApi("admin", document)
            .ShouldBe(["admin /mediakit", "admin /artwork/x"], ignoreOrder: true);
    }

    [Fact]
    public void FindUnsupportedShapes_UnsupportedShapeDocument_ReportsEveryUninterpretedShape()
    {
        using var document = JsonDocument.Parse(UnsupportedShapeDocument);

        ApiContractPolicyRules.FindUnsupportedShapes("admin", document).ShouldBe(
        [
            "admin paths[/api/things/{thingId}].parameters[0]: $ref '#/components/parameters/ThingId' is not an " +
            "existing components/schemas entry",
            "admin paths[/api/things/{thingId}].get.parameters[1].schema: $ref '#/components/schemas/MissingLimit' " +
            "is not an existing components/schemas entry",
            "admin paths[/api/things/{thingId}].get.responses.404: $ref '#/components/responses/NotFound' is not an " +
            "existing components/schemas entry",
            "admin paths[/api/things/{thingId}].get.responses.default.content.application/json.schema: $ref " +
            "'#/components/schemas/Missing' is not an existing components/schemas entry",
            "admin paths[/api/things/{thingId}].post.responses.202.content.application/json.schema: $ref " +
            "'#/components/schemas/MissingJob' is not an existing components/schemas entry",
            "admin components.schemas.Thing.properties.owner: $ref '#/components/schemas/MissingOwner' is not an " +
            "existing components/schemas entry",
            "admin paths[/api/things/{thingId}].get: documents a default response",
            "admin paths[/api/things/{thingId}].parameters[0]: parameter is a $ref"
        ], ignoreOrder: true);
    }

    [Fact]
    public void FindViolations_UnsupportedShapeDocument_DoesNotThrowAndFailsClosed()
    {
        using var document = JsonDocument.Parse(UnsupportedShapeDocument);
        IReadOnlyList<ApiOperation> operations = ApiOperation.FromDocument("admin", document);

        foreach (PolicyRule rule in ApiContractPolicyRules.All)
            Should.NotThrow(() => rule.FindViolations(operations).ToList(), rule.Id);
        Rule("R6").FindViolations(operations).ShouldBe(["admin POST /api/things/{thingId}"]);
        Rule("R10").FindViolations(operations).ShouldBe(["admin GET /api/things/{thingId} ?limit"]);
    }

    [Fact]
    public void FindAllowlistDrift_NewViolation_NamesTheRuleRemedyAndPolicy()
    {
        IReadOnlyList<string> drift = ApiContractPolicyRules.FindAllowlistDrift(
            Rule("R11"),
            ["admin ThingPart.expectedRevision"],
            new Dictionary<string, string>(StringComparer.Ordinal));

        drift.ShouldBe([
            "R11 violation 'admin ThingPart.expectedRevision': every request revision field, parameter or header is " +
            "a reviewed batch or snapshot binding. Remedy: Move the precondition to If-Match; only a genuine batch " +
            "revision or snapshot binding goes in ReviewedRequestRevisionFields, with a rationale (Idempotency And " +
            "Concurrency). See docs/decisions/api-contract-policy.md; migration allowlists only shrink."
        ]);
    }

    [Fact]
    public void FindAllowlistDrift_StaleEntry_NamesTheDictionaryHoldingIt()
    {
        IReadOnlyList<string> drift = ApiContractPolicyRules.FindAllowlistDrift(
            Rule("R4"),
            ["admin DELETE /api/things"],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "admin DELETE /api/things", "ApiContractPolicyAllowlists.R4" },
                { "admin DELETE /api/retired", "ApiContractPolicyAllowlists.R4" }
            });

        drift.ShouldBe([
            "R4 entry 'admin DELETE /api/retired' in ApiContractPolicyAllowlists.R4 no longer violates. Delete it."
        ]);
    }

    [Fact]
    public void FindAllowlistDrift_ReviewedExemptionOnARouteDocumenting404_IsStale()
    {
        using var document = JsonDocument.Parse(SyntheticDocument);
        IReadOnlyList<ApiOperation> operations = ApiOperation.FromDocument("admin", document);
        const string ledger = "ApiContractPolicyAllowlists.ReviewedPathParameterNotFoundExemptions";

        IReadOnlyList<string> drift = ApiContractPolicyRules.FindAllowlistDrift(
            Rule("R9"),
            Rule("R9").FindViolations(operations),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "admin GET /api/admin/things/{thingId}", ledger },
                { "admin POST /api/things/{thingId}/rebuild", ledger }
            });

        drift.ShouldBe([
            $"R9 entry 'admin POST /api/things/{{thingId}}/rebuild' in {ledger} no longer violates. Delete it."
        ]);
    }

    [Fact]
    public void AllowedViolations_R9_IncludesTheReviewedExemptionLedger()
    {
        IReadOnlyDictionary<string, string> holders = ApiContractPolicyAllowlists.AllowedViolations("R9");

        holders.Count.ShouldBe(
            ApiContractPolicyAllowlists.R9.Count + ApiContractPolicyAllowlists.ReviewedPathParameterNotFoundExemptions.Count);
        holders.Where(holder => ApiContractPolicyAllowlists.R9.ContainsKey(holder.Key))
            .ShouldAllBe(holder => holder.Value == "ApiContractPolicyAllowlists.R9");
    }

    [Fact]
    public void AllowedViolations_R11_NamesThePendingListOrTheReviewedLedger()
    {
        IReadOnlyDictionary<string, string> holders = ApiContractPolicyAllowlists.AllowedViolations("R11");

        holders["consumer ConsumerDiscSetAccessRequest.revision"]
            .ShouldBe("ApiContractPolicyAllowlists.ReviewedRequestRevisionFields");
        holders.Where(holder => ApiContractPolicyAllowlists.R11.ContainsKey(holder.Key))
            .ShouldAllBe(holder => holder.Value == "ApiContractPolicyAllowlists.R11");
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("consumer")]
    public void R11_CatalogSnapshotBindings_ReviewDoesNotExemptOtherFieldsPathsOrMethods(string host)
    {
        using var document = JsonDocument.Parse("""
            {"paths":{
              "/api/catalog-snapshot/systems":{
                "get":{"parameters":[
                  {"name":"revision","in":"query","required":true,"schema":{"type":"string"}},
                  {"name":"unreviewedRevision","in":"query","schema":{"type":"string"}}
                ],"responses":{"200":{},"409":{}}},
                "post":{"parameters":[
                  {"name":"revision","in":"query","schema":{"type":"string"}}
                ],"responses":{"200":{}}}
              },
              "/api/catalog-snapshot/systems-extra":{"get":{"parameters":[
                {"name":"revision","in":"query","schema":{"type":"string"}}
              ],"responses":{"200":{}}}}
            }}
            """);
        var findings = Rule("R11").FindViolations(ApiOperation.FromDocument(host, document)).ToArray();
        findings.ShouldContain(host + " GET /api/catalog-snapshot/systems ?revision");
        var holders = ApiContractPolicyAllowlists.AllowedViolations("R11");
        findings.Where(field => !holders.ContainsKey(field)).ShouldBe([
            host + " GET /api/catalog-snapshot/systems ?unreviewedRevision",
            host + " POST /api/catalog-snapshot/systems ?revision",
            host + " GET /api/catalog-snapshot/systems-extra ?revision"
        ], ignoreOrder: true);
        ApiContractPolicyAllowlists.ReviewedRequestRevisionFields[host + " GET /api/catalog-snapshot/systems ?revision"]
            .Classification.ShouldBe(RevisionFieldClassification.SnapshotBinding);
        ApiContractPolicyAllowlists.R11.ShouldBeEmpty();
    }

    private static PolicyRule Rule(string ruleId) =>
        ApiContractPolicyRules.All.Single(rule => rule.Id == ruleId);
}
