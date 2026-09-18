using System.Text.Json;

namespace Romd.Persistence.Entities;

internal static class ReferenceJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
