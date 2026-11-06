using System.Text.Json;
using Shouldly;
using Xunit;
using static Romd.Hosting.IntegrationTests.Infrastructure.OpenApiTestDocuments;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Enforces the OpenAPI shape rules of <c>docs/decisions/api-contract-policy.md</c> over the live
///     documents of both hosts. Today's violations sit in shrinking migration allowlists: a new violation
///     fails, and so does an entry that no longer violates.
/// </summary>
public sealed class ApiContractPolicyGuardrailTests(ApiContractPolicyGuardrailTests.LiveOpenApiDocuments documents)
    : IClassFixture<ApiContractPolicyGuardrailTests.LiveOpenApiDocuments>
{
    public static TheoryData<string> RuleIds => [.. ApiContractPolicyRules.All.Select(rule => rule.Id)];

    [Theory]
    [MemberData(nameof(RuleIds))]
    public void LiveOpenApi_PolicyRule_OnlyAllowlistedViolationsRemain(string ruleId)
    {
        PolicyRule rule = ApiContractPolicyRules.All.Single(candidate => candidate.Id == ruleId);

        IReadOnlyList<string> drift = ApiContractPolicyRules.FindAllowlistDrift(
            rule,
            rule.FindViolations(documents.Operations),
            ApiContractPolicyAllowlists.AllowedViolations(ruleId));

        drift.ShouldBeEmpty();
    }

    [Fact]
    public void LiveOpenApi_RoutesOutsideApi_AreExemptProtocolSurfaces()
    {
        IEnumerable<string> routes = documents.Documents.SelectMany(document =>
            ApiContractPolicyRules.FindRoutesOutsideApi(document.Key, document.Value));

        routes.ShouldBeEmpty(
            "Only protocol surfaces may live outside /api; every other route follows " +
            $"{ApiContractPolicyRules.PolicyDocument}.");
    }

    [Fact]
    public void LiveOpenApi_DocumentShapes_AreTheShapesTheRulesInterpret()
    {
        IEnumerable<string> shapes = documents.Documents.SelectMany(document =>
            ApiContractPolicyRules.FindUnsupportedShapes(document.Key, document.Value));

        shapes.ShouldBeEmpty("The policy rules only interpret component-schema references, explicit statuses and " +
            "inline parameters; teach them a new generator shape before relying on it.");
    }

    [Fact]
    public void PathParameterNotFoundLedger_ReviewedAndPendingEntries_AreDisjointAndExplained()
    {
        ApiContractPolicyAllowlists.ReviewedPathParameterNotFoundExemptions.Keys
            .Intersect(ApiContractPolicyAllowlists.R9.Keys, StringComparer.Ordinal)
            .ShouldBeEmpty("An operation is either a reviewed R9 exemption or pending migration, never both.");
        ApiContractPolicyAllowlists.ReviewedPathParameterNotFoundExemptions.Values
            .ShouldAllBe(rationale => !string.IsNullOrWhiteSpace(rationale));
    }

    [Fact]
    public void RequestRevisionLedger_ReviewedAndPendingEntries_AreDisjointAndExplained()
    {
        ApiContractPolicyAllowlists.ReviewedRequestRevisionFields.Keys
            .Intersect(ApiContractPolicyAllowlists.R11.Keys, StringComparer.Ordinal)
            .ShouldBeEmpty("A request revision field is either reviewed as permanent or pending migration, never both.");
        ApiContractPolicyAllowlists.ReviewedRequestRevisionFields.Values
            .ShouldAllBe(field => !string.IsNullOrWhiteSpace(field.Rationale));
    }

    public sealed class LiveOpenApiDocuments : IAsyncLifetime
    {
        private readonly Dictionary<string, JsonDocument> _documents = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, JsonDocument> Documents => _documents;

        internal IReadOnlyList<ApiOperation> Operations { get; private set; } = [];

        public async Task InitializeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            _documents["admin"] = await ReadLiveOpenApiAsync<Romd.Admin.Host.Program>("admin-v1", timeout.Token);
            _documents["consumer"] = await ReadLiveOpenApiAsync<Romd.Consumer.Host.Program>("consumer-v1", timeout.Token);
            Operations = [.. _documents.SelectMany(document => ApiOperation.FromDocument(document.Key, document.Value))];
        }

        public Task DisposeAsync()
        {
            foreach (JsonDocument document in _documents.Values)
                document.Dispose();
            return Task.CompletedTask;
        }
    }
}
