using Romd.Domain.Source.Rom;
using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Infrastructure.ReferenceData;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.ReferenceData;

public sealed class RedumpDiscMediaReferenceParserTests
{
    private const string Source = "https://redump.info/disc/42", Provenance = "synthetic-authenticated-local-import/v1";
    private const string Crc = "0000002a", Md5Hex = "11111111111111111111111111111111", Sha1Hex = "2222222222222222222222222222222222222222";
    private static readonly Guid RecordId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset RetrievedAt = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("DVD-5", RomDiscReferenceMedia.Dvd5)]
    [InlineData("DVD-9", RomDiscReferenceMedia.Dvd9)]
    public void Parse_ExplicitSingletonDvd_BindsPublishedQuartetAndIndependentExactRawBody(string media, RomDiscReferenceMedia kind)
    {
        byte[] raw = Encoding.UTF8.GetBytes(Page(media: media));

        var result = RedumpDiscMediaReferenceParser.Parse(raw, RecordId, Source, 42, RetrievedAt, Provenance);

        result.IsError.ShouldBeFalse();
        var record = result.Value;
        record.Id.ShouldBe(RecordId);
        record.System.ShouldBe(RomDiscReferenceSystem.PlayStation2);
        record.Media.ShouldBe(kind);
        record.FileCount.ShouldBe(1);
        record.LogicalBytes.ShouldBe(1048576);
        record.Crc32.ShouldBe(Crc32.Parse(Crc));
        record.Md5.ShouldBe(Md5.Parse(Md5Hex));
        record.Sha1.ShouldBe(Sha1.Parse(Sha1Hex));
        record.RawReferenceBytes.ShouldBe(raw.Length);
        record.RawReferenceSha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(raw)));
        record.ReferenceSource.ShouldBe(Source);
        record.ParserProfile.ShouldBe("redump-disc-singleton-dvd-html/v1");
        record.AcceptedSourceProvenance.ShouldBe(Provenance);
        record.RetrievedAt.ShouldBe(RetrievedAt);
    }

    [Theory]
    [InlineData("renamed.iso")]
    [InlineData("arbitrary.bin")]
    [InlineData("unrelated.txt")]
    public void Parse_RenamedSingleton_DoesNotInferIdentityOrMediaFromFilename(string filename)
    {
        var original = Parse(Page());
        var renamed = Parse(Page(filename: filename));

        renamed.Value.Media.ShouldBe(original.Value.Media);
        renamed.Value.LogicalBytes.ShouldBe(original.Value.LogicalBytes);
        renamed.Value.Crc32.ShouldBe(original.Value.Crc32);
        renamed.Value.Md5.ShouldBe(original.Value.Md5);
        renamed.Value.Sha1.ShouldBe(original.Value.Sha1);
        renamed.Value.RawReferenceSha256.ShouldNotBe(original.Value.RawReferenceSha256);
    }

    [Fact]
    public void Parse_PublishedZeroCrc_IsPresentRatherThanMissing()
    {
        var result = Parse(Page(crc: "00000000"));

        result.Value.Crc32.IsEmpty.ShouldBeFalse();
        result.Value.Crc32.ShouldBe(Crc32.FromUInt32(0));
    }

    [Theory]
    [InlineData("Sony PlayStation 2", "CD")]
    [InlineData("Sony PlayStation", "CD")]
    [InlineData("Sony PlayStation", "DVD-5")]
    [InlineData("Sony PlayStation 3", "DVD-9")]
    [InlineData("Sony PlayStation 2", "DVD")]
    [InlineData("Sony PlayStation 2", "Unknown")]
    public void Parse_OtherExplicitSystemOrMedia_RejectsWithoutConstructingDvdRecord(string system, string media) =>
        Parse(Page(system: system, media: media)).FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.Unsupported());

    [Theory]
    [InlineData("missing-system")]
    [InlineData("missing-media")]
    [InlineData("duplicate-system")]
    [InlineData("conflicting-system")]
    [InlineData("duplicate-media")]
    [InlineData("conflicting-media")]
    [InlineData("duplicate-info-table")]
    [InlineData("duplicate-files-table")]
    [InlineData("missing-info-table")]
    [InlineData("missing-files-table")]
    public void Parse_MissingDuplicatedOrContradictoryDiscAssertions_FailsClosed(string changed)
    {
        string html = Page();
        html = changed switch
        {
            "missing-system" => html.Replace("<strong>System</strong>", "<strong>Other</strong>"),
            "missing-media" => html.Replace("<strong>Media</strong>", "<strong>Other</strong>"),
            "duplicate-info-table" => html.Replace("<!--INFO-END-->", Info()),
            "duplicate-files-table" => html.Replace("<!--FILES-END-->", Files()),
            "missing-info-table" => html.Replace("disc-info-table", "unrelated-table"),
            "missing-files-table" => html.Replace("files-table", "unrelated-table"),
            _ => html
        };
        if (changed is "duplicate-system" or "conflicting-system" or "duplicate-media" or "conflicting-media")
        {
            string label = changed.EndsWith("system", StringComparison.Ordinal) ? "SYSTEM" : "Media";
            string value = changed switch { "conflicting-system" => "Sony PlayStation", "conflicting-media" => "CD", "duplicate-system" => "Sony PlayStation 2", _ => "DVD-5" };
            html = Page().Replace("</table><!--INFO-END-->", $"<tr><td><strong>{label}</strong></td><td>{value}</td></tr></table><!--INFO-END-->");
        }

        Parse(html).FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.Malformed());
    }

    [Theory]
    [InlineData("missing-row")]
    [InlineData("extra-identical-row")]
    [InlineData("extra-conflicting-row")]
    [InlineData("synthetic-row")]
    [InlineData("missing-cell")]
    [InlineData("extra-cell")]
    [InlineData("unknown-header")]
    [InlineData("duplicate-header")]
    [InlineData("swapped-header")]
    [InlineData("extra-header")]
    [InlineData("extra-section")]
    public void Parse_UnknownMissingOrMultipleFileFacts_DoesNotPickAConvenientRow(string changed)
    {
        string html = Page();
        html = changed switch
        {
            "missing-row" => html.Replace(Row(), ""),
            "extra-identical-row" => html.Replace("</tbody>", Row() + "</tbody>"),
            "extra-conflicting-row" => html.Replace("</tbody>", Row(size: "2097152") + "</tbody>"),
            "synthetic-row" => html.Replace("<tbody><tr>", "<tbody><tr class='files-synthetic-row'>"),
            "missing-cell" => html.Replace($"<td><code>{Sha1Hex}</code></td>", ""),
            "extra-cell" => html.Replace("</tr></tbody>", "<td>extra</td></tr></tbody>"),
            "unknown-header" => html.Replace("<th>SHA-1</th>", "<th>SHA256</th>"),
            "duplicate-header" => html.Replace("<th>MD5</th>", "<th>CRC-32</th>"),
            "swapped-header" => html.Replace("<th>MD5</th><th>SHA-1</th>", "<th>SHA-1</th><th>MD5</th>"),
            "extra-header" => html.Replace("</tr></thead>", "<th>Extra</th></tr></thead>"),
            "extra-section" => html.Replace("</tbody>", "</tbody><tfoot><tr><td>Total</td></tr></tfoot>"),
            _ => html
        };

        Parse(html).FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.Malformed());
    }

    [Theory]
    [InlineData("size", "")]
    [InlineData("size", "0")]
    [InlineData("size", "-1048576")]
    [InlineData("size", "+1048576")]
    [InlineData("size", "1,048,576")]
    [InlineData("size", "1e6")]
    [InlineData("size", "10737418241")]
    [InlineData("size", "999999999999999999999999")]
    [InlineData("crc", "")]
    [InlineData("crc", "00002a")]
    [InlineData("crc", "000000zz")]
    [InlineData("md5", "")]
    [InlineData("md5", "00000000000000000000000000000000")]
    [InlineData("md5", "wrong")]
    [InlineData("sha1", "")]
    [InlineData("sha1", "0000000000000000000000000000000000000000")]
    [InlineData("sha1", "wrong")]
    [InlineData("filename", "")]
    public void Parse_IncompleteOrMalformedPublishedQuartet_RejectsInsteadOfDefaulting(string field, string invalid) =>
        Parse(Page(size: field == "size" ? invalid : "1048576", crc: field == "crc" ? invalid : Crc,
            md5: field == "md5" ? invalid : Md5Hex, sha1: field == "sha1" ? invalid : Sha1Hex,
            filename: field == "filename" ? invalid : "synthetic.iso")).FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.Malformed());

    [Theory]
    [InlineData("comment")]
    [InlineData("script")]
    [InlineData("style")]
    [InlineData("attribute")]
    [InlineData("nested-table")]
    public void Parse_RequiredTablesExistOnlyInIgnoredOrNestedSpoofContent_CannotSupplyAssertions(string context)
    {
        string tables = Info() + Files();
        string html = context switch
        {
            "comment" => "<!--" + tables + "-->",
            "script" => "<script>const ignored = `" + tables + "`;</script>",
            "style" => "<style>/*" + tables + "*/</style>",
            "attribute" => "<div title=\"" + tables.Replace("\"", "&quot;") + "\"></div>",
            _ => "<table><tr><td>" + tables + "</td></tr></table>"
        };

        Parse("<!DOCTYPE html><html><body>" + html + "</body></html>").FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.Malformed());
    }

    [Theory]
    [InlineData("comment")]
    [InlineData("script")]
    [InlineData("style")]
    public void Parse_IgnoredSpoofTablesBesideRealCdAssertion_DoNotReplaceExplicitMedia(string context)
    {
        string spoof = Info() + Files();
        string ignored = context switch { "comment" => "<!--" + spoof + "-->", "script" => "<script>" + spoof + "</script>", _ => "<style>" + spoof + "</style>" };

        Parse(Page(media: "CD").Replace("</body>", ignored + "</body>")).FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.Unsupported());
    }

    [Theory]
    [InlineData("<!DOCTYPE html SYSTEM 'file:///etc/passwd'>")]
    [InlineData("<!DOCTYPE html PUBLIC 'unsafe' 'https://evil.example/dtd'>")]
    [InlineData("<!DOCTYPE html [<!ENTITY external SYSTEM 'file:///etc/passwd'>]>")]
    [InlineData("<?xml version='1.0'?>")]
    [InlineData("<?xml-stylesheet href='https://evil.example/style'?>")]
    [InlineData("<!ENTITY external SYSTEM 'https://evil.example/entity'>")]
    [InlineData("<![CDATA[ignored]]>")]
    public void Parse_DtdEntityOrProcessingInstruction_RejectsWithoutResolvingExternalContent(string declaration) =>
        Parse(Page().Replace("<!DOCTYPE html>", declaration)).FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.Malformed());

    [Theory]
    [InlineData("http://redump.info/disc/42")]
    [InlineData("https://www.redump.info/disc/42")]
    [InlineData("https://redump.info:443/disc/42")]
    [InlineData("https://evil.example/disc/42")]
    [InlineData("https://user@redump.info/disc/42")]
    [InlineData("https://redump.info/disc/042")]
    [InlineData("https://redump.info/disc/43")]
    [InlineData("https://redump.info/disc/42/")]
    [InlineData("https://redump.info/disc/42?query=true")]
    [InlineData("https://redump.info/disc/42#fragment")]
    [InlineData("https://redump.info/disc/%34%32")]
    [InlineData("https://redump.info/disc/42/../43")]
    [InlineData("file:///disc/42")]
    [InlineData(null)]
    public void Parse_NoncanonicalUnsafeOrForeignSourceUrl_RejectsCallerContext(string? source) =>
        Parse(Page(), source: source!).FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.InvalidContext());

    [Theory]
    [InlineData("id")]
    [InlineData("disc-zero")]
    [InlineData("disc-negative")]
    [InlineData("disc-mismatch")]
    [InlineData("time-not-utc")]
    [InlineData("provenance-empty")]
    [InlineData("provenance-control")]
    [InlineData("provenance-limit")]
    public void Parse_InvalidRetainedIdentityTimeOrProvenance_RejectsBeforeRecordCreation(string field) =>
        RedumpDiscMediaReferenceParser.Parse(Encoding.UTF8.GetBytes(Page()), field == "id" ? Guid.Empty : RecordId, Source,
            field switch { "disc-zero" => 0, "disc-negative" => -1, "disc-mismatch" => 43, _ => 42 },
            field == "time-not-utc" ? RetrievedAt.ToOffset(TimeSpan.FromHours(1)) : RetrievedAt,
            field switch { "provenance-empty" => " ", "provenance-control" => "source\n", "provenance-limit" => new string('p', 257), _ => Provenance })
            .FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.InvalidContext());

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("//evil.example/path")]
    [InlineData("https://evil.example/path")]
    [InlineData("/\\evil.example/path")]
    public void Parse_UnsafeLinkInConsumedTable_RejectsWithoutFollowingIt(string href) =>
        Parse(Page().Replace("/discs?system=PS2", href)).FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.Malformed());

    [Theory]
    [InlineData("field-control")]
    [InlineData("raw-null")]
    [InlineData("raw-delete")]
    [InlineData("unclosed-table")]
    [InlineData("unclosed-quote")]
    [InlineData("nested-row")]
    [InlineData("script-field")]
    [InlineData("namespace")]
    [InlineData("duplicate-class")]
    [InlineData("mixed-table-role")]
    [InlineData("deep-field")]
    public void Parse_MalformedControlledOrAmbiguousMarkup_FailsClosed(string field)
    {
        string html = Page();
        html = field switch
        {
            "field-control" => html.Replace("Sony PlayStation 2", "Sony PlayStation 2&#x09;"),
            "raw-null" => html + "\0",
            "raw-delete" => html + "\u007f",
            "unclosed-table" => html.Replace("</table><!--FILES-END-->", "<!--FILES-END-->"),
            "unclosed-quote" => html.Replace("class='disc-table files-table'", "class='disc-table files-table"),
            "nested-row" => html.Replace("<td>Games</td>", "<td><tr><td>Media</td><td>CD</td></tr></td>"),
            "script-field" => html.Replace("Sony PlayStation 2</a>", "<script>Sony PlayStation 2</script></a>"),
            "namespace" => html.Replace("<tbody>", "<tbody xmlns='urn:spoof'>"),
            "duplicate-class" => html.Replace("class='disc-table files-table'", "class='disc-table files-table' CLASS='other'"),
            "mixed-table-role" => html.Replace("class='disc-table files-table'", "class='disc-info-table files-table'"),
            "deep-field" => html.Replace(Md5Hex, string.Concat(Enumerable.Repeat("<code>", 17)) + Md5Hex + string.Concat(Enumerable.Repeat("</code>", 17))),
            _ => html
        };

        Parse(html).FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.Malformed());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Parse_InvalidUtf8_RejectsRatherThanReplacingBytes(int invalid)
    {
        byte[] raw = invalid switch { 0 => [0xff], 1 => [0xc0, 0xaf], _ => [0xe2, 0x82] };

        RedumpDiscMediaReferenceParser.Parse(raw, RecordId, Source, 42, RetrievedAt, Provenance)
            .FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.InvalidUtf8());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_BodySizeBoundary_RejectsOverflowAndHashesAcceptedExactBoundary(bool overflow)
    {
        byte[] prefix = Encoding.UTF8.GetBytes(Page());
        byte[] raw = Enumerable.Repeat((byte)' ', RedumpDiscMediaReferenceParser.MaximumBodyBytes + (overflow ? 1 : 0)).ToArray();
        prefix.CopyTo(raw, 0);

        var result = RedumpDiscMediaReferenceParser.Parse(raw, RecordId, Source, 42, RetrievedAt, Provenance);

        if (overflow)
            result.FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.InputLimit());
        else
        { result.Value.RawReferenceBytes.ShouldBe(raw.Length); result.Value.RawReferenceSha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(raw))); }
    }

    [Fact]
    public void Parse_EmptyBody_RejectsMissingReference() =>
        RedumpDiscMediaReferenceParser.Parse(ReadOnlyMemory<byte>.Empty, RecordId, Source, 42, RetrievedAt, Provenance)
            .FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.InputLimit());

    [Fact]
    public void Parse_PreCancelledCaller_PropagatesExactCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Should.Throw<OperationCanceledException>(() => RedumpDiscMediaReferenceParser.Parse(
            Encoding.UTF8.GetBytes(Page()), RecordId, Source, 42, RetrievedAt, Provenance, cancellation.Token));

        exception.CancellationToken.ShouldBe(cancellation.Token);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("textarea")]
    [InlineData("xmp")]
    [InlineData("iframe")]
    [InlineData("noembed")]
    [InlineData("noframes")]
    [InlineData("plaintext")]
    [InlineData("template")]
    [InlineData("noscript")]
    [InlineData("svg")]
    [InlineData("math")]
    public void Parse_HtmlContext_TargetTablesOnlyInInertOrForeignContent_CannotSupplyAssertions(string context) =>
        Parse("<!DOCTYPE html><html><body><" + context + ">" + Info() + Files() + "</" + context + "></body></html>")
            .FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.Malformed());

    [Theory]
    [InlineData("title")]
    [InlineData("textarea")]
    [InlineData("xmp")]
    [InlineData("iframe")]
    [InlineData("noembed")]
    [InlineData("noframes")]
    [InlineData("template")]
    [InlineData("noscript")]
    public void Parse_HtmlContext_InertDvdTablesBesideOrdinaryCd_DoNotReplaceExplicitMedia(string context) =>
        Parse(Page(media: "CD").Replace("</body>", "<" + context + ">" + Info() + Files() + "</" + context + "></body>"))
            .FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.Unsupported());

    [Theory]
    [InlineData("title")]
    [InlineData("textarea")]
    [InlineData("xmp")]
    [InlineData("iframe")]
    [InlineData("noembed")]
    [InlineData("noframes")]
    [InlineData("template")]
    [InlineData("noscript")]
    public void Parse_HtmlContext_InertConflictingTablesBesideOrdinaryDvd_BindsOnlyOuterQuartet(string context)
    {
        string html = Page().Replace("</body>", "<" + context + ">" + Info(media: "DVD-9") +
            Files(size: "2097152", crc: "0000002b", md5: new string('3', 32), sha1: new string('4', 40)) + "</" + context + "></body>");
        byte[] raw = Encoding.UTF8.GetBytes(html);

        var result = RedumpDiscMediaReferenceParser.Parse(raw, RecordId, Source, 42, RetrievedAt, Provenance);

        result.IsError.ShouldBeFalse();
        result.Value.Media.ShouldBe(RomDiscReferenceMedia.Dvd5);
        result.Value.LogicalBytes.ShouldBe(1048576);
        result.Value.Crc32.ShouldBe(Crc32.Parse(Crc));
        result.Value.Md5.ShouldBe(Md5.Parse(Md5Hex));
        result.Value.Sha1.ShouldBe(Sha1.Parse(Sha1Hex));
        result.Value.RawReferenceBytes.ShouldBe(raw.Length);
        result.Value.RawReferenceSha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(raw)));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("textarea")]
    [InlineData("xmp")]
    [InlineData("iframe")]
    [InlineData("noembed")]
    [InlineData("noframes")]
    [InlineData("template")]
    [InlineData("noscript")]
    public void Parse_HtmlContext_UnterminatedInertElementContainingCompleteDvd_FailsClosed(string context) =>
        Parse("<!DOCTYPE html><html><body><" + context + ">synthetic text" + Info() + Files() + "</body></html>")
            .FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.Malformed());

    [Theory]
    [InlineData("plaintext", "DVD-5")]
    [InlineData("plaintext", "CD")]
    [InlineData("svg", "DVD-5")]
    [InlineData("svg", "CD")]
    [InlineData("math", "DVD-5")]
    [InlineData("math", "CD")]
    public void Parse_HtmlContext_UnsupportedGrammarBesideOrdinaryTables_IsExplicitlyMalformed(string context, string media) =>
        Parse(Page(media: media).Replace("</body>", "<" + context + ">synthetic inert text</" + context + "></body>"))
            .FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.Malformed());

    [Theory]
    [InlineData("Synthetic Disc &amp; ordinary title", "DVD-5")]
    [InlineData("Synthetic <strong>literal title markup</strong>", "DVD-9")]
    [InlineData("Synthetic Disc &amp; ordinary title", "CD")]
    [InlineData("Synthetic <strong>literal title markup</strong>", "CD")]
    public void Parse_HtmlContext_OrdinaryTitleAndOuterTables_PreservesCurrentLayout(string title, string media)
    {
        string html = Page(media: media).Replace("</head>", "<title>" + title + "</title></head>");

        var result = Parse(html);

        if (media == "CD")
            result.FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.Unsupported());
        else
        {
            result.IsError.ShouldBeFalse();
            result.Value.Media.ShouldBe(media == "DVD-5" ? RomDiscReferenceMedia.Dvd5 : RomDiscReferenceMedia.Dvd9);
            result.Value.LogicalBytes.ShouldBe(1048576);
            result.Value.RawReferenceSha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(Encoding.UTF8.GetBytes(html))));
        }
    }

    [Theory]
    [InlineData("nested-template")]
    [InlineData("nested-noscript")]
    [InlineData("mixed-contexts")]
    [InlineData("closing-text")]
    public void Parse_HtmlContext_BalancedInertChildrenAndClosingText_CannotExposeSpoofTables(string shape)
    {
        string tables = Info() + Files();
        string ignored = shape switch
        {
            "nested-template" => "<template><template>" + tables + "</template></template>",
            "nested-noscript" => "<noscript><noscript>" + tables + "</noscript></noscript>",
            "mixed-contexts" => "<template><noscript>" + tables + "</noscript></template>",
            _ => "<template><!-- </template> --><script>const marker = '</template>';</script>" +
                "<div data-ignored='</template>'></div><textarea></template></textarea>" + tables + "</template>"
        };

        Parse(Page(media: "CD").Replace("</body>", ignored + "</body>"))
            .FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.Unsupported());
    }

    [Theory]
    [InlineData("title")]
    [InlineData("textarea")]
    [InlineData("xmp")]
    [InlineData("iframe")]
    [InlineData("noembed")]
    [InlineData("noframes")]
    [InlineData("template")]
    [InlineData("noscript")]
    public void Parse_HtmlContext_SelfClosingInertElementBeforeOrdinaryDvd_FailsClosed(string context) =>
        Parse(Page().Replace("<body>", "<body><" + context + "/>"))
            .FirstError.ShouldBe(RedumpDiscMediaReferenceErrors.Malformed());

    private static ErrorOr<RomDiscMediaReferenceRecord> Parse(string html, string source = Source) =>
        RedumpDiscMediaReferenceParser.Parse(Encoding.UTF8.GetBytes(html), RecordId, source, 42, RetrievedAt, Provenance);

    private static string Page(string system = "Sony PlayStation 2", string media = "DVD-5", string filename = "synthetic.iso",
        string size = "1048576", string crc = Crc, string md5 = Md5Hex, string sha1 = Sha1Hex) =>
        "<!DOCTYPE html><html><head><meta charset='utf-8'></head><body>" + Info(system, media) + "<!--INFO-END-->" +
        Files(filename, size, crc, md5, sha1) + "<!--FILES-END--></body></html>";

    private static string Info(string system = "Sony PlayStation 2", string media = "DVD-5") =>
        "<table class='table-compact disc-info-table'><tr><td><strong>System</strong></td><td><a href='/discs?system=PS2'>" +
        system + "</a></td></tr><tr><td><strong>Media</strong></td><td>" + media +
        "</td></tr><tr><td><strong>Category</strong></td><td>Games</td></tr>" +
        "<tr><td><strong>Disc Serial</strong></td><td>synthetic<br>ignored<img alt='synthetic' src='/flag.svg'></td></tr></table>";

    private static string Files(string filename = "synthetic.iso", string size = "1048576", string crc = Crc,
        string md5 = Md5Hex, string sha1 = Sha1Hex) => "<table class='disc-table files-table'><thead><tr>" +
        "<th>Filename</th><th>Size</th><th>CRC-32</th><th>MD5</th><th>SHA-1</th></tr></thead><tbody>" +
        Row(filename, size, crc, md5, sha1) + "</tbody></table>";

    private static string Row(string filename = "synthetic.iso", string size = "1048576", string crc = Crc,
        string md5 = Md5Hex, string sha1 = Sha1Hex) => "<tr><td><code>" + filename + "</code></td><td><code>" + size +
        "</code></td><td><code>" + crc + "</code></td><td><code>" + md5 + "</code></td><td><code>" + sha1 + "</code></td></tr>";
}
