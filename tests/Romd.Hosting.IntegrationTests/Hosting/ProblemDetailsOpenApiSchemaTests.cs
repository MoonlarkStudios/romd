using System.Text.Json;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Both documents publish the ROMD envelope extensions on the shared ProblemDetails component
///     (docs/decisions/api-contract-policy.md, "One Error Envelope"), so generated clients type
///     errorCode, errors and traceId on every documented error response.
/// </summary>
public sealed class ProblemDetailsOpenApiSchemaTests
{
    [Theory]
    [InlineData("web/schemas/admin-v1.json")]
    [InlineData("web/schemas/consumer-v1.json")]
    public void CommittedSnapshot_ProblemDetailsDeclaresEnvelopeExtensions(string snapshotPath)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(RepositoryPaths.FindRepositoryFile(snapshotPath)));

        AssertEnvelopeSchema(document.RootElement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveDocument_ProblemDetailsDeclaresEnvelopeExtensions(bool consumer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var document = consumer
            ? await OpenApiTestDocuments.ReadLiveOpenApiAsync<Romd.Consumer.Host.Program>("consumer-v1", timeout.Token)
            : await OpenApiTestDocuments.ReadLiveOpenApiAsync<Romd.Admin.Host.Program>("admin-v1", timeout.Token);

        AssertEnvelopeSchema(document.RootElement);
    }

    private static void AssertEnvelopeSchema(JsonElement root)
    {
        var schema = root
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ProblemDetails");
        var properties = schema.GetProperty("properties");

        properties.GetProperty("errorCode").GetProperty("type").GetString().ShouldBe("string");
        properties.GetProperty("traceId").GetProperty("type").GetString().ShouldBe("string");

        var errors = properties.GetProperty("errors");
        errors.GetProperty("type").GetString().ShouldBe("object");
        var messages = errors.GetProperty("additionalProperties");
        messages.GetProperty("type").GetString().ShouldBe("array");
        messages.GetProperty("items").GetProperty("type").GetString().ShouldBe("string");

        schema.GetProperty("required")
            .EnumerateArray()
            .Select(name => name.GetString())
            .Order(StringComparer.Ordinal)
            .ShouldBe(["errorCode", "traceId"]);
    }
}
