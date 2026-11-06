using System.Collections.Frozen;

namespace Romd.Consumer.Application.Vfs;

/// <summary>Scoped with its layout implementations and their repository dependencies.</summary>
public sealed class VfsLayoutRegistry
{
    private readonly FrozenDictionary<string, IVfsLayout> _layouts;

    public VfsLayoutRegistry(IEnumerable<IVfsLayout> layouts)
    {
        var registered = new Dictionary<string, IVfsLayout>(StringComparer.Ordinal);
        foreach (IVfsLayout layout in layouts)
        {
            string key = layout.Key;
            if (string.IsNullOrEmpty(key) || key.Length > 64 ||
                key[0] is < 'a' or > 'z' ||
                key.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
                throw new InvalidOperationException($"Invalid VFS layout key: '{key}'.");
            if (!registered.TryAdd(key, layout))
                throw new InvalidOperationException($"Duplicate VFS layout key: '{key}'.");
        }

        _layouts = registered.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public IVfsLayout? Find(string key) => _layouts.GetValueOrDefault(key);
}
