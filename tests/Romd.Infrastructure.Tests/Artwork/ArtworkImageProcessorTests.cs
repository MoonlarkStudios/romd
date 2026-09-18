using System.Buffers.Binary;
using System.IO.Hashing;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Artwork;
using Shouldly;
using SkiaSharp;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed class ArtworkImageProcessorTests
{
    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "image/png")]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg")]
    [InlineData(SKEncodedImageFormat.Webp, "image/webp")]
    public async Task ProcessAsync_SmallStaticImage_MeasuresWithoutUpscalingOrChangingInput(SKEncodedImageFormat format, string contentType)
    {
        var bytes = Image(40, 20, format);
        var original = bytes.ToArray();

        var result = await new ArtworkImageProcessor().ProcessAsync(bytes, ArtworkRole.Poster);

        result.IsError.ShouldBeFalse();
        result.Value.ContentType.ShouldBe(contentType);
        result.Value.Width.ShouldBe(40);
        result.Value.Height.ShouldBe(20);
        bytes.ShouldBe(original);
        result.Value.Variants.Select(variant => variant.Name).ShouldBe(["thumb", "card"]);
        foreach (var variant in result.Value.Variants)
        {
            variant.Width.ShouldBe(40);
            variant.Height.ShouldBe(20);
            variant.ContentType.ShouldBe("image/png");
            using var decoded = SKBitmap.Decode(variant.EncodedBytes);
            decoded.Width.ShouldBe(40);
            decoded.Height.ShouldBe(20);
        }
    }

    [Theory]
    [InlineData(ArtworkRole.Poster, 800, 1200, "card", 600, 900)]
    [InlineData(ArtworkRole.Poster, 800, 400, "card", 600, 300)]
    [InlineData(ArtworkRole.Hero, 2400, 1200, "hero", 1240, 620)]
    [InlineData(ArtworkRole.Logo, 1920, 600, "logo", 960, 300)]
    [InlineData(ArtworkRole.Logo, 200, 80, "logo", 200, 80)]
    public async Task ProcessAsync_LargeImage_BoundsDeliveryAndPreservesProportions(ArtworkRole role,
        int width, int height, string name, int expectedWidth, int expectedHeight)
    {
        var result = await new ArtworkImageProcessor().ProcessAsync(Image(width, height), role);

        result.IsError.ShouldBeFalse();
        var variant = result.Value.Variants.Single(candidate => candidate.Name == name);
        variant.Width.ShouldBe(expectedWidth);
        variant.Height.ShouldBe(expectedHeight);
    }

    [Theory]
    [InlineData(1, 40, 20, true)]
    [InlineData(2, 40, 20, false)]
    [InlineData(3, 40, 20, false)]
    [InlineData(4, 40, 20, true)]
    [InlineData(5, 20, 40, true)]
    [InlineData(6, 20, 40, true)]
    [InlineData(7, 20, 40, false)]
    [InlineData(8, 20, 40, false)]
    public async Task ProcessAsync_ExifOrientation_RotatesOrMirrorsPixelsAndDimensions(int orientation,
        int width, int height, bool redAtTopLeft)
    {
        var jpeg = Image(40, 20, SKEncodedImageFormat.Jpeg);
        byte[] exif = [69, 120, 105, 102, 0, 0, 73, 73, 42, 0, 8, 0, 0, 0,
            1, 0, 18, 1, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0];
        var bytes = new byte[jpeg.Length + exif.Length + 4];
        jpeg.AsSpan(0, 2).CopyTo(bytes);
        bytes[2] = 255;
        bytes[3] = 225;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), (ushort)(exif.Length + 2));
        exif.CopyTo(bytes, 6);
        jpeg.AsSpan(2).CopyTo(bytes.AsSpan(6 + exif.Length));

        var result = await new ArtworkImageProcessor().ProcessAsync(bytes, ArtworkRole.Poster);

        result.IsError.ShouldBeFalse();
        result.Value.Width.ShouldBe(width);
        result.Value.Height.ShouldBe(height);
        using var decoded = SKBitmap.Decode(result.Value.Variants[0].EncodedBytes);
        decoded.Width.ShouldBe(width);
        decoded.Height.ShouldBe(height);
        var corner = decoded.GetPixel(3, 3);
        (corner.Red > corner.Blue).ShouldBe(redAtTopLeft);
    }

    [Fact]
    public async Task ProcessAsync_TruncatedPngOrJpeg_Rejects()
    {
        foreach (var format in new[] { SKEncodedImageFormat.Png, SKEncodedImageFormat.Jpeg })
        {
            var bytes = Image(40, 20, format);
            var result = await new ArtworkImageProcessor().ProcessAsync(bytes.AsMemory(0, bytes.Length - 8), ArtworkRole.Poster);
            result.IsError.ShouldBeTrue();
            result.FirstError.Code.ShouldBe("Artwork.InvalidImage");
        }
    }

    [Fact]
    public async Task ProcessAsync_AnimatedPngMarkerEvenWithSingleImage_Rejects()
    {
        var png = Image(40, 20);
        var animation = Chunk("acTL", [0, 0, 0, 1, 0, 0, 0, 0]);
        var bytes = png[..33].Concat(animation).Concat(png[33..]).ToArray();

        var result = await new ArtworkImageProcessor().ProcessAsync(bytes, ArtworkRole.Poster);

        result.FirstError.Code.ShouldBe("Artwork.InvalidImage");
    }

    [Fact]
    public async Task ProcessAsync_AnimatedWebpMarker_Rejects()
    {
        var webp = Image(40, 20, SKEncodedImageFormat.Webp);
        var bytes = webp.Concat(new byte[] { 65, 78, 73, 77, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0 }).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length - 8);

        var result = await new ArtworkImageProcessor().ProcessAsync(bytes, ArtworkRole.Hero);

        result.FirstError.Code.ShouldBe("Artwork.InvalidImage");
    }

    [Theory]
    [InlineData(20000, 10)]
    [InlineData(10000, 10000)]
    public async Task ProcessAsync_ExcessiveDeclaredDimensions_RejectsBeforePixelAllocation(int width, int height)
    {
        var png = Image(40, 20);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), (uint)height);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), Crc32.HashToUInt32(png.AsSpan(12, 17)));

        var result = await new ArtworkImageProcessor().ProcessAsync(png, ArtworkRole.Poster);

        result.FirstError.Code.ShouldBe("Artwork.ImageTooLarge");
    }

    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'/>")]
    [InlineData("GIF89a")]
    [InlineData("not an image")]
    public async Task ProcessAsync_UnsupportedContent_Rejects(string value)
    {
        var result = await new ArtworkImageProcessor().ProcessAsync(System.Text.Encoding.UTF8.GetBytes(value), ArtworkRole.Poster);
        result.FirstError.Code.ShouldBe("Artwork.InvalidImage");
    }

    [Fact]
    public async Task ProcessAsync_EncodedLimit_Rejects()
    {
        var result = await new ArtworkImageProcessor().ProcessAsync(new byte[32 * 1024 * 1024 + 1], ArtworkRole.Poster);
        result.FirstError.Code.ShouldBe("Artwork.ImageTooLarge");
    }

    [Fact]
    public async Task ProcessAsync_TransparentImage_PreservesAlpha()
    {
        using var bitmap = new SKBitmap(10, 10);
        bitmap.Erase(SKColors.Transparent);
        bitmap.SetPixel(5, 5, SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        var result = await new ArtworkImageProcessor().ProcessAsync(encoded.ToArray(), ArtworkRole.Logo);

        result.IsError.ShouldBeFalse();
        result.Value.Variants.Select(variant => variant.Name).ShouldBe(["thumb", "logo"]);
        using var decoded = SKBitmap.Decode(result.Value.Variants[0].EncodedBytes);
        decoded.GetPixel(0, 0).Alpha.ShouldBe((byte)0);
        decoded.GetPixel(5, 5).Red.ShouldBe((byte)255);
    }

    [Fact]
    public async Task ProcessAsync_CorruptedPixelPayloadWithValidContainer_RejectsDecoderFailure()
    {
        var png = Image(40, 20);
        var offset = 8;
        while (!png.AsSpan(offset + 4, 4).SequenceEqual("IDAT"u8))
            offset += (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)) + 12;
        var size = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
        png.AsSpan(offset + 8, size).Fill(0xff);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(offset + size + 8),
            Crc32.HashToUInt32(png.AsSpan(offset + 4, size + 4)));

        var result = await new ArtworkImageProcessor().ProcessAsync(png, ArtworkRole.Poster);

        result.FirstError.Code.ShouldBe("Artwork.InvalidImage");
    }

    [Fact]
    public async Task ProcessAsync_Cancelled_ThrowsBeforeDecoding()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => new ArtworkImageProcessor()
            .ProcessAsync(Image(40, 20), ArtworkRole.Poster, cancellation.Token));
    }

    [Theory]
    [InlineData(4000, 2250, 3840)]
    [InlineData(1200, 800, 1200)]
    public async Task ProcessAsync_Backdrop_ProducesDistinctWebpWidthsWithoutUpscaling(int width, int height, int maximum)
    {
        var result = await new ArtworkImageProcessor().ProcessAsync(Image(width, height), ArtworkRole.Backdrop);
        result.IsError.ShouldBeFalse();
        var variants = result.Value.Variants;
        variants.Max(item => item.Width).ShouldBe(maximum);
        variants.Select(item => item.Width).Distinct().Count().ShouldBe(variants.Count);
        foreach (var variant in variants)
        {
            variant.ContentType.ShouldBe("image/webp");
            variant.Width.ShouldBeLessThanOrEqualTo(width);
            variant.Height.ShouldBeLessThanOrEqualTo(height);
            using var decoded = SKBitmap.Decode(variant.EncodedBytes);
            decoded.Width.ShouldBe(variant.Width);
            decoded.Height.ShouldBe(variant.Height);
            Math.Abs((double)variant.Width / variant.Height - (double)width / height).ShouldBeLessThan(0.01);
        }
    }

    private static byte[] Image(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Blue);
            using var red = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(new SKRect(0, 0, width / 2f, height), red);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 100);
        return encoded.ToArray();
    }

    private static byte[] Chunk(string type, byte[] payload)
    {
        var bytes = new byte[payload.Length + 12];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)payload.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
        payload.CopyTo(bytes, 8);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(bytes.Length - 4), Crc32.HashToUInt32(bytes.AsSpan(4, payload.Length + 4)));
        return bytes;
    }
}
