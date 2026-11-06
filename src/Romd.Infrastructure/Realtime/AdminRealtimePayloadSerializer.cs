using System.Text.Json;

namespace Romd.Infrastructure.Realtime;

public static class AdminRealtimePayloadSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize<TPayload>(TPayload payload) =>
        JsonSerializer.Serialize(payload, JsonOptions);

    public static TPayload Deserialize<TPayload>(string payloadJson) =>
        JsonSerializer.Deserialize<TPayload>(payloadJson, JsonOptions)
        ?? throw new JsonException($"Could not deserialize admin realtime payload as {typeof(TPayload).Name}.");
}
