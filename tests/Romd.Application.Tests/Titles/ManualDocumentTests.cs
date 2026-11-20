using System.Text;
using Romd.Admin.Application.Titles.Commands.UploadTitleMedia;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public class ManualDocumentTests
{
    [Theory]
    [InlineData("1.4")]
    [InlineData("1.7")]
    [InlineData("2.0")]
    public async Task IsAcceptableAsync_CompletePdf_IsAccepted(string version) =>
        (await AcceptsAsync(Pdf(version))).ShouldBeTrue();

    [Fact]
    public async Task IsAcceptableAsync_LeadingJunkWithinTheHeaderWindow_IsAccepted() =>
        (await AcceptsAsync([.. "\r\n\r\n"u8, .. Pdf("1.6")])).ShouldBeTrue();

    [Theory]
    [InlineData("%PDF-3.0")]
    [InlineData("%PNG-1.4")]
    public async Task IsAcceptableAsync_UnknownHeader_IsRejected(string header) =>
        (await AcceptsAsync(Pdf("1.4", header: header))).ShouldBeFalse();

    [Fact]
    public async Task IsAcceptableAsync_TruncatedDocument_IsRejected()
    {
        var complete = Pdf("1.4");
        (await AcceptsAsync(complete[..^20])).ShouldBeFalse();
    }

    [Theory]
    [InlineData("/Encrypt 5 0 R")]
    [InlineData("/OpenAction << /S /JavaScript /JS (app.alert(1)) >>")]
    [InlineData("/AA << /O << /S /Launch /F (calc) >> >>")]
    public async Task IsAcceptableAsync_EncryptedOrActiveDocument_IsRejected(string dictionary) =>
        (await AcceptsAsync(Pdf("1.7", catalogExtra: dictionary))).ShouldBeFalse();

    [Fact]
    public async Task IsAcceptableAsync_ALongerNameThatStartsLikeARefusedOne_IsAccepted() =>
        (await AcceptsAsync(Pdf("1.7", catalogExtra: "/JavaScriptless true /Launcher (x)"))).ShouldBeTrue();

    [Fact]
    public async Task IsAcceptableAsync_RefusedNameAcrossAReadBoundary_IsRejected()
    {
        var padding = new string(' ', (1 << 20) - 40);
        (await AcceptsAsync(Pdf("1.7", catalogExtra: padding + "/JavaScript (x)"))).ShouldBeFalse();
    }

    [Fact]
    public async Task IsAcceptableAsync_DeclaredLengthOverTheLimit_IsRejected()
    {
        await using var stream = new MemoryStream(Pdf("1.4"));
        (await ManualDocument.IsAcceptableAsync(stream, ManualDocument.MaxBytes + 1, default)).ShouldBeFalse();
    }

    private static async Task<bool> AcceptsAsync(byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes);
        return await ManualDocument.IsAcceptableAsync(stream, bytes.Length, default);
    }

    /// <summary>A minimal one-page PDF with a classic cross-reference table and trailer.</summary>
    private static byte[] Pdf(string version, string? header = null, string catalogExtra = "")
    {
        var body = new StringBuilder();
        body.Append(header ?? $"%PDF-{version}").Append("\n%âãÏÓ\n");
        var offsets = new List<int>();
        void Object(string content)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(body.ToString()));
            body.Append($"{offsets.Count} 0 obj\n{content}\nendobj\n");
        }

        Object($"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>");
        Object("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        var xref = Encoding.Latin1.GetByteCount(body.ToString());
        body.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            body.Append($"{offset:D10} 00000 n \n");
        body.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(body.ToString());
    }
}
