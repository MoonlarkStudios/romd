using System.Collections.Frozen;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>
///     Immutable registration of exactly one descriptor per <see cref="RomRepresentationEncoding" />, so callers read
///     encoding facts instead of branching on a concrete encoding. A missing, duplicate or undefined encoding fails composition.
/// </summary>
public sealed class RomEncodingRegistry
{
    private readonly FrozenDictionary<RomRepresentationEncoding, RomEncodingDescriptor> _byEncoding;

    public RomEncodingRegistry(IEnumerable<RomEncodingDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var declared = descriptors.ToArray();
        if (declared.Any(descriptor => descriptor is null || !Enum.IsDefined(descriptor.Encoding)) ||
            declared.DistinctBy(descriptor => descriptor.Encoding).Count() != declared.Length ||
            declared.Length != Enum.GetValues<RomRepresentationEncoding>().Length)
            throw new ArgumentException("Every ROM representation encoding requires exactly one descriptor.", nameof(descriptors));
        _byEncoding = declared.ToFrozenDictionary(descriptor => descriptor.Encoding);
    }

    /// <exception cref="ArgumentOutOfRangeException">The encoding is not a defined representation encoding.</exception>
    public RomEncodingDescriptor Describe(RomRepresentationEncoding encoding) =>
        _byEncoding.TryGetValue(encoding, out var descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "The ROM representation encoding is undefined.");
}
