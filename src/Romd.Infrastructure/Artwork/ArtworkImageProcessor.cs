using System.Buffers.Binary;
using System.IO.Hashing;
using ErrorOr;
using Romd.Admin.Application.Artwork;
using Romd.Domain.Catalog;
using SkiaSharp;

namespace Romd.Infrastructure.Artwork;

/// <summary>
/// One decode at a time per process bounds native pixel allocations independently of worker concurrency.
/// Cancellation is observed around synchronous native calls; it cannot interrupt a decode already running.
/// </summary>
public sealed class ArtworkImageProcessor : IArtworkImageProcessor
{
    private const int MaxEncodedBytes = 32 * 1024 * 1024;
    private const int MaxDimension = 16384;
    private const long MaxPixels = 40_000_000;
    private static readonly SemaphoreSlim DecodeGate = new(1, 1);

    public async Task<ErrorOr<ProcessedArtworkImage>> ProcessAsync(ReadOnlyMemory<byte> original, ArtworkRole role,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(role)) return ArtworkImageErrors.UnsupportedRole();
        if (original.Length > MaxEncodedBytes) return ArtworkImageErrors.TooLarge();
        if (original.IsEmpty) return ArtworkImageErrors.Invalid();
        await DecodeGate.WaitAsync(ct);
        try
        {
            return Process(original.Span, role, ct);
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    private static ErrorOr<ProcessedArtworkImage> Process(ReadOnlySpan<byte> bytes, ArtworkRole role, CancellationToken ct)
    {
        if (!IsCompleteStaticContainer(bytes)) return ArtworkImageErrors.Invalid();
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null) return ArtworkImageErrors.Invalid();
        var contentType = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Webp => "image/webp",
            _ => null
        };
        if (contentType is null) return ArtworkImageErrors.Invalid();
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || info.Width > MaxDimension || info.Height > MaxDimension ||
            (long)info.Width * info.Height > MaxPixels) return ArtworkImageErrors.TooLarge();
        if (codec.FrameCount > 1) return ArtworkImageErrors.Invalid();
        ct.ThrowIfCancellationRequested();
        var decodedInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap();
        if (!bitmap.TryAllocPixels(decodedInfo)) return ArtworkImageErrors.ProcessingFailed();
        if (codec.GetPixels(decodedInfo, bitmap.GetPixels()) != SKCodecResult.Success)
            return ArtworkImageErrors.Invalid();
        ct.ThrowIfCancellationRequested();
        bitmap.SetImmutable();
        var swapped = (int)codec.EncodedOrigin >= 5;
        var width = swapped ? info.Height : info.Width;
        var height = swapped ? info.Width : info.Height;
        using var image = SKImage.FromBitmap(bitmap);
        var limits = role switch
        {
            ArtworkRole.Poster => new[] { ("thumb", 300, 450), ("card", 600, 900) },
            ArtworkRole.Hero => [("thumb", 960, 310), ("hero", 1920, 620)],
            ArtworkRole.Logo => [("thumb", 480, 240), ("logo", 960, 480)],
            ArtworkRole.Backdrop => [("preview", 480, 480), ("small", 960, 960), ("backdrop", 1920, 1920), ("large", 2880, 2880), ("ultra", 3840, 3840)],
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
        var variants = new List<ProcessedArtworkVariant>();
        foreach (var (name, maxWidth, maxHeight) in limits)
        {
            ct.ThrowIfCancellationRequested();
            var scale = Math.Min(1d, Math.Min((double)maxWidth / width, (double)maxHeight / height));
            var targetWidth = Math.Max(1, (int)Math.Floor(width * scale));
            var targetHeight = Math.Max(1, (int)Math.Floor(height * scale));
            if (role == ArtworkRole.Backdrop && variants.Any(v => v.Width == targetWidth && v.Height == targetHeight)) continue;
            using var output = new SKBitmap();
            if (!output.TryAllocPixels(new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul)))
                return ArtworkImageErrors.ProcessingFailed();
            using (var canvas = new SKCanvas(output))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Scale((float)targetWidth / width, (float)targetHeight / height);
                var matrix = Orientation(codec.EncodedOrigin, info.Width, info.Height);
                canvas.Concat(matrix);
                canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            }
            output.SetImmutable();
            using var encodedImage = SKImage.FromBitmap(output);
            var backdrop = role == ArtworkRole.Backdrop;
            using var encoded = encodedImage.Encode(backdrop ? SKEncodedImageFormat.Webp : SKEncodedImageFormat.Png, backdrop ? 85 : 100);
            if (encoded is null) return ArtworkImageErrors.Invalid();
            variants.Add(new ProcessedArtworkVariant(name, backdrop ? "image/webp" : "image/png", targetWidth, targetHeight, encoded.ToArray()));
            ct.ThrowIfCancellationRequested();
        }
        return new ProcessedArtworkImage(contentType, width, height, variants);
    }

    private static SKMatrix Orientation(SKEncodedOrigin origin, int width, int height) => (int)origin switch
    {
        2 => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
        3 => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
        4 => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
        5 => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        6 => new SKMatrix(0, -1, height, 1, 0, 0, 0, 0, 1),
        7 => new SKMatrix(0, -1, height, -1, 0, width, 0, 0, 1),
        8 => new SKMatrix(0, 1, 0, -1, 0, width, 0, 0, 1),
        _ => SKMatrix.CreateIdentity()
    };

    private static bool IsCompleteStaticContainer(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 137 && bytes.Slice(1, 7).SequenceEqual("PNG\r\n\x1a\n"u8))
        {
            var offset = 8;
            while (bytes.Length - offset >= 12)
            {
                var size = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
                if (size > (uint)(bytes.Length - offset - 12)) return false;
                var type = bytes.Slice(offset + 4, 4);
                if (type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8)) return false;
                if (Crc32.HashToUInt32(bytes.Slice(offset + 4, (int)size + 4)) !=
                    BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + (int)size, 4))) return false;
                offset += (int)size + 12;
                if (type.SequenceEqual("IEND"u8)) return size == 0 && offset == bytes.Length;
            }
            return false;
        }
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) != bytes.Length - 8) return false;
            var offset = 12;
            while (bytes.Length - offset >= 8)
            {
                var type = bytes.Slice(offset, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
                if (size > (uint)(bytes.Length - offset - 8)) return false;
                if (type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8) ||
                    (type.SequenceEqual("VP8X"u8) && size > 0 && (bytes[offset + 8] & 2) != 0)) return false;
                offset += 8 + (int)size + (int)(size & 1);
            }
            return offset == bytes.Length;
        }
        return bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[^2] == 0xff && bytes[^1] == 0xd9;
    }
}
