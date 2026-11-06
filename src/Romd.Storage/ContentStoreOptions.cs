using System.ComponentModel.DataAnnotations;

namespace Romd.Storage;

/// <summary>
///     Configuration for content-addressable storage.
/// </summary>
public sealed class ContentStoreOptions
{
    /// <summary>
    ///     Root directory for content storage.
    /// </summary>
    [Required]
    public required string RootPath { get; set; }

    /// <summary>
    ///     Representation for new objects. Defaults to seekable Zstandard, falling back to raw when
    ///     compression misses <see cref="MinCompressionRatio" />. Content larger than four frames falls back
    ///     early when its leading four frames miss it. Existing objects remain unchanged.
    /// </summary>
    [EnumDataType(typeof(ContentWritePolicy))]
    public ContentWritePolicy WritePolicy { get; set; } = ContentWritePolicy.SeekableZstandard;

    /// <summary>
    ///     Zstd compression level (1-22). Default is 3 (balanced speed/ratio).
    /// </summary>
    [Range(1, 22)]
    public int CompressionLevel { get; set; } = 3;

    /// <summary>Uncompressed bytes per independent frame. Default 256 KiB.</summary>
    [Range(64 * 1024, 1024 * 1024)]
    public int SeekableFrameSize { get; set; } = 256 * 1024;

    /// <summary>
    ///     Byte budget for seek tables shared by positional readers, which the VFS opens once per request.
    ///     Default 64 MiB, about 21 tables of 64 GiB objects at 256 KiB frames. Zero disables sharing.
    /// </summary>
    [Range(0, long.MaxValue)]
    public long SeekIndexCacheBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    ///     Buffer size for stream operations. Default is 80KB.
    /// </summary>
    [Range(4096, 10 * 1024 * 1024)]
    public int BufferSize { get; set; } = 81920;

    /// <summary>
    ///     Maximum size of content admitted by a write. Default is 4GB.
    ///     Reads never apply it: each stored object bounds its own reads.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long MaxContentSize { get; set; } = 4L * 1024 * 1024 * 1024;

    /// <summary>
    ///     File extension for uncompressed content (when compression provides no benefit).
    /// </summary>
    public string UncompressedExtension { get; set; } = ".raw";

    /// <summary>
    ///     Minimum compression ratio to keep compressed version.
    ///     If compression doesn't achieve at least this ratio, store uncompressed.
    ///     Default 0.95 (5% savings minimum). Set to 1.0 to retain compression whenever it does not increase size,
    ///     except that content larger than four frames whose leading frames do not compress is stored raw early.
    /// </summary>
    [Range(0.0, 1.0)]
    public double MinCompressionRatio { get; set; } = 0.95;
}

public enum ContentWritePolicy
{
    SeekableZstandard,
    Raw
}
