using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Content;

public sealed class RomDeliveryFormatCatalogTests
{
    [Fact]
    public void FileExtension_EncodedFormat_ReturnsItsRegisteredExtension()
    {
        var catalog = new RomDeliveryFormatCatalog([
            new(RomDeliveryFormat.Original, "Original", null), new(RomDeliveryFormat.Rvz, "RVZ", ".rvz"),
            new(RomDeliveryFormat.Chd, "CHD", ".chd")]);

        catalog.FileExtension(RomDeliveryFormat.Rvz).ShouldBe(".rvz");
        catalog.FileExtension(RomDeliveryFormat.Chd).ShouldBe(".chd");
    }

    [Fact]
    public void FileExtension_Original_Throws()
    {
        var catalog = TestDeliveryPolicies.Formats;

        Should.Throw<ArgumentOutOfRangeException>(() => catalog.FileExtension(RomDeliveryFormat.Original));
    }

    [Theory]
    [InlineData(RomDeliveryFormat.Original, "Plain", null)]
    [InlineData(RomDeliveryFormat.Rvz, "Dolphin RVZ", ".rvz")]
    [InlineData(RomDeliveryFormat.Chd, "CHD", ".chd")]
    public void Entry_RegisteredFormat_ReturnsItsLabelAndExtension(RomDeliveryFormat format, string label, string? extension)
    {
        var catalog = new RomDeliveryFormatCatalog([
            new(RomDeliveryFormat.Original, "Plain", null), new(RomDeliveryFormat.Rvz, "Dolphin RVZ", ".rvz"),
            new(RomDeliveryFormat.Chd, "CHD", ".chd")]);

        catalog.Entry(format).ShouldBe(new RomDeliveryFormatEntry(format, label, extension));
    }

    [Fact]
    public void Entry_UndefinedFormat_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => TestDeliveryPolicies.Formats.Entry((RomDeliveryFormat)99));

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("undefined")]
    [InlineData("original-extension")]
    [InlineData("encoded-without-extension")]
    [InlineData("no-dot")]
    [InlineData("path")]
    [InlineData("shared-extension")]
    [InlineData("uppercase")]
    [InlineData("too-long")]
    [InlineData("null-entry")]
    [InlineData("null-label")]
    [InlineData("empty-label")]
    [InlineData("blank-label")]
    [InlineData("padded-label")]
    [InlineData("control-label")]
    [InlineData("too-long-label")]
    [InlineData("shared-label")]
    public void Constructor_IncompleteOrAmbiguousCatalog_Throws(string defect)
    {
        RomDeliveryFormatEntry?[] entries = defect switch
        {
            "missing" => [new(RomDeliveryFormat.Original, "Original", null), new(RomDeliveryFormat.Rvz, "RVZ", ".rvz")],
            "duplicate" => [new(RomDeliveryFormat.Original, "Original", null), new(RomDeliveryFormat.Rvz, "RVZ", ".rvz"),
                new(RomDeliveryFormat.Rvz, "RVZ again", ".rvz2")],
            "undefined" => [new(RomDeliveryFormat.Original, "Original", null), new(RomDeliveryFormat.Rvz, "RVZ", ".rvz"),
                new((RomDeliveryFormat)99, "Other", ".x")],
            "original-extension" => Valid(original: ".iso"),
            "encoded-without-extension" => Valid(rvz: null),
            "no-dot" => Valid(rvz: "rvz"),
            "path" => Valid(rvz: ".r/z"),
            "shared-extension" => Valid(rvz: ".img", chd: ".img"),
            "uppercase" => Valid(rvz: ".RVZ"),
            "too-long" => Valid(rvz: "." + new string('r', 16)),
            "null-label" => Valid(rvzLabel: null),
            "empty-label" => Valid(rvzLabel: ""),
            "blank-label" => Valid(rvzLabel: "  "),
            "padded-label" => Valid(rvzLabel: " RVZ"),
            "control-label" => Valid(rvzLabel: "R\nVZ"),
            "too-long-label" => Valid(rvzLabel: new string('R', RomDeliveryFormatCatalog.MaximumLabelLength + 1)),
            "shared-label" => Valid(rvzLabel: "chd"),
            _ => [new(RomDeliveryFormat.Original, "Original", null), null, new(RomDeliveryFormat.Rvz, "RVZ", ".rvz"),
                new(RomDeliveryFormat.Chd, "CHD", ".chd")]
        };

        Should.Throw<ArgumentException>(() => new RomDeliveryFormatCatalog(entries!));
    }

    [Fact]
    public void Constructor_LabelAtTheLengthBound_IsAccepted()
    {
        string label = new('R', RomDeliveryFormatCatalog.MaximumLabelLength);

        new RomDeliveryFormatCatalog(Valid(rvzLabel: label)!).Entry(RomDeliveryFormat.Rvz).Label.ShouldBe(label);
    }

    private static RomDeliveryFormatEntry?[] Valid(string? original = null, string? rvz = ".rvz", string? chd = ".chd",
        string? rvzLabel = "RVZ") =>
        [new(RomDeliveryFormat.Original, "Original", original), new(RomDeliveryFormat.Rvz, rvzLabel!, rvz),
            new(RomDeliveryFormat.Chd, "CHD", chd)];
}
