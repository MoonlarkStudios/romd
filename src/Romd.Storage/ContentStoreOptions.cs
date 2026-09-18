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
    ///     Zstd compression level (1-22). Default is 3 (balanced speed/ratio).
    /// </summary>
    [Range(1, 22)]
    public int CompressionLevel { get; set; } = 3;

    /// <summary>
    ///     Buffer size for stream operations. Default is 80KB.
    /// </summary>
    [Range(4096, 10 * 1024 * 1024)]
    public int BufferSize { get; set; } = 81920;

    /// <summary>
    ///     Maximum allowed content size. Default is 4GB.
    ///     Protects against decompression bombs and resource exhaustion.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long MaxContentSize { get; set; } = 4L * 1024 * 1024 * 1024;

    /// <summary>
    ///     File extension for compressed content.
    /// </summary>
    public string CompressedExtension { get; set; } = ".zst";

    /// <summary>
    ///     File extension for uncompressed content (when compression provides no benefit).
    /// </summary>
    public string UncompressedExtension { get; set; } = ".raw";

    /// <summary>
    ///     Minimum compression ratio to keep compressed version.
    ///     If compression doesn't achieve at least this ratio, store uncompressed.
    ///     Default 0.95 (5% savings minimum). Set to 1.0 to always compress.
    /// </summary>
    [Range(0.0, 1.0)]
    public double MinCompressionRatio { get; set; } = 0.95;
}
