using Microsoft.Extensions.Configuration;
using Romd.Storage;

namespace Romd.Hosting;

/// <summary>Binds <c>Romd:Storage</c> write settings onto content store options.</summary>
internal static class RomdStorageConfiguration
{
    internal const string SectionName = "Romd:Storage";
    internal const long DefaultMaxContentSize = 64L * 1024 * 1024 * 1024;

    internal static void Apply(ContentStoreOptions options, IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        options.WritePolicy = section.GetValue(nameof(ContentStoreOptions.WritePolicy), options.WritePolicy);
        options.SeekableFrameSize = section.GetValue(nameof(ContentStoreOptions.SeekableFrameSize), options.SeekableFrameSize);
        if (options.SeekableFrameSize is < 64 * 1024 or > 1024 * 1024)
            throw new InvalidOperationException($"{SectionName}:SeekableFrameSize must be between 65536 and 1048576 bytes.");
        options.MaxContentSize = section.GetValue(nameof(ContentStoreOptions.MaxContentSize), options.MaxContentSize);
        if (options.MaxContentSize <= 0)
            throw new InvalidOperationException($"{SectionName}:MaxContentSize must be a positive number of bytes.");
        options.SeekIndexCacheBytes = section.GetValue(nameof(ContentStoreOptions.SeekIndexCacheBytes), options.SeekIndexCacheBytes);
        if (options.SeekIndexCacheBytes < 0)
            throw new InvalidOperationException($"{SectionName}:SeekIndexCacheBytes must be zero or a positive number of bytes.");
    }
}
