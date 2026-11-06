using System.Text.Json;

namespace Romd.Hosting.IntegrationTests.Infrastructure;

internal static class OpenApiTestDocuments
{
    public static async Task<JsonDocument> ReadLiveOpenApiAsync<TProgram>(string documentName, CancellationToken ct)
        where TProgram : class
    {
        string dataDirectory = Path.Combine(Path.GetTempPath(), $"romd-delivery-openapi-{Guid.NewGuid():N}");
        try
        {
            await using var factory = RomdHostTestFactory.Create<TProgram>(
                dataDirectory,
                "Development",
                "delivery-openapi-test-secret-at-least-32-characters");
            using var client = factory.CreateClient();
            return JsonDocument.Parse(await client.GetStringAsync($"/openapi/{documentName}.json", ct));
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }

    public static JsonElement Resolve(JsonElement schema, JsonElement schemas) =>
        schema.TryGetProperty("$ref", out JsonElement reference)
            ? schemas.GetProperty(reference.GetString()!.Split('/').Last())
            : schema;
}
