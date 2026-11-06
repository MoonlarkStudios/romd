namespace Romd.Application.Common.Systems;

public sealed class SystemKeys(IReadOnlyDictionary<int, string> keys)
{
    public string Required(int id) => keys.TryGetValue(id, out var key) ? key : throw new InvalidOperationException($"Unregistered system identity {id}.");
    public string? Optional(int? id) => id.HasValue ? Required(id.Value) : null;
}
