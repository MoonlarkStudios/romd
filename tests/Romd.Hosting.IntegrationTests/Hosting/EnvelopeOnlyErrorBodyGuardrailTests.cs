using Shouldly;
using Xunit;
using static Romd.Hosting.IntegrationTests.Infrastructure.RepositoryPaths;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Source scan for envelope-only error bodies (docs/decisions/api-contract-policy.md,
///     "One Error Envelope"; docs/decisions/api-contract-policy.md, "Guardrails"): endpoint code
///     in <c>src/Romd.Hosting</c> and both host <c>Program.cs</c> files builds error bodies only
///     through <c>ProblemResults</c> and <c>ToProblem</c>. The scope is fail-closed: a new file is
///     scanned unless it is the envelope helper, a derived protocol surface, or listed
///     non-endpoint protocol code, and every exemption must still exempt something. Today's
///     violations are pinned per file as an exact count of each form, split by the slice that
///     removes them, so the allowlist only shrinks and swapping one violation for another fails.
/// </summary>
public sealed class EnvelopeOnlyErrorBodyGuardrailTests
{
    /// <summary>The envelope helper itself, which alone constructs problem bodies.</summary>
    private static readonly string[] HelperOwners =
    [
        "src/Romd.Hosting/Endpoints/ProblemResults.cs"
    ];

    /// <summary>
    ///     Endpoint files whose every mapped route is a protocol surface, as
    ///     <see cref="EnvelopeSourceScan.IsProtocolSurface" /> derives them. Pinned so a change to
    ///     the derivation is reviewed.
    /// </summary>
    private static readonly string[] ExpectedProtocolSurfaces =
    [
        "src/Romd.Hosting/Endpoints/ArtworkDeliveryEndpoints.cs",
        "src/Romd.Hosting/Endpoints/ConsumerStorageEndpoints.cs",
        "src/Romd.Hosting/Endpoints/ConsumerVfsEndpoints.cs",
        "src/Romd.Hosting/Endpoints/OpenIddictEndpoints.cs",
        "src/Romd.Hosting/Endpoints/StorageEndpoints.cs",
        "src/Romd.Hosting/Hosting/RomdHealthEndpointRouteExtensions.cs"
    ];

    /// <summary>Protocol code that maps no routes itself, keyed by path or directory prefix.</summary>
    private static readonly IReadOnlyDictionary<string, string> NonEndpointProtocolCode =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Romd.Hosting/Vfs/"] =
                "WebDAV request handling and Basic authentication for /vfs; WebDAV clients read status codes.",
            ["src/Romd.Hosting/Security/InteractiveAuthenticationForms.cs"] =
                "Size and antiforgery guards for the /connect login and verification HTML forms."
        };

    /// <summary>
    ///     Pinned violations: file → each form's exact count, split by the slice that removes it.
    ///     A form's counts across owners sum to the occurrences found.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, PinnedForm[]> Allowlist =
        new Dictionary<string, PinnedForm[]>(StringComparer.Ordinal);

    [Fact]
    public void EndpointCode_BuildsErrorBodiesOnlyThroughEnvelopeHelpers()
    {
        var found = ScannedFiles().ToDictionary(
            file => file,
            file => EnvelopeSourceScan.FindViolations(ReadSource(file)),
            StringComparer.Ordinal);
        var allowed = Allowlist
            .SelectMany(entry => entry.Value.Select(pinned => (File: entry.Key, pinned.Form, pinned.Count)))
            .GroupBy(pinned => (pinned.File, pinned.Form))
            .ToDictionary(group => group.Key, group => group.Sum(pinned => pinned.Count));
        var actual = found
            .SelectMany(scan => scan.Value.GroupBy(violation => violation.Form)
                .Select(group => (Key: (File: scan.Key, Form: group.Key), Violations: group.ToArray())))
            .ToDictionary(entry => entry.Key, entry => entry.Violations);

        string[] overBudget = actual
            .Where(entry => entry.Value.Length > allowed.GetValueOrDefault(entry.Key))
            .Select(entry => $"{entry.Key.File}: {entry.Value.Length} [{entry.Key.Form}] outside the envelope helpers " +
                             $"(allowed {allowed.GetValueOrDefault(entry.Key)})" +
                             string.Concat(entry.Value.Select(violation => $"\n    {violation.Line}: {violation.Text}")))
            .ToArray();
        string[] stale = allowed
            .Where(entry => actual.GetValueOrDefault(entry.Key, []).Length < entry.Value)
            .Select(entry => $"{entry.Key.File}: stale [{entry.Key.Form}] pins {entry.Value}, " +
                             $"found {actual.GetValueOrDefault(entry.Key, []).Length}; lower or delete it.")
            .ToArray();

        overBudget.Concat(stale).ShouldBeEmpty(string.Join("\n", overBudget.Concat(stale)));
    }

    [Fact]
    public void ProtocolExemption_IsDerivedFromMappedRouteLiterals()
    {
        AllInScopeFiles()
            .Where(file => EnvelopeSourceScan.IsProtocolSurface(ReadSource(file)))
            .ShouldBe(ExpectedProtocolSurfaces);
    }

    [Theory]
    [InlineData("/media/reconciliation")]
    [InlineData("/media/reconciliation/subjects/{kind}/{id}")]
    [InlineData("/api/media/reconciliation")]
    public void ProtocolExemption_DoesNotTreatMediaManagementGroupsAsByteRoutes(string path)
    {
        EnvelopeSourceScan.IsProtocolSurface($"app.MapGroup(\"{path}\");").ShouldBeFalse();
        EnvelopeSourceScan.IsProtocolSurface("app.MapGet(\"/media/{mediaId}\", Serve);").ShouldBeTrue();
    }

    [Fact]
    public void FixedExemptions_AreNonEndpointCodeAndEachExemptsAViolation()
    {
        foreach (string owner in HelperOwners)
        {
            EnvelopeSourceScan.FindViolations(ReadSource(owner))
                .ShouldNotBeEmpty($"{owner} exempts nothing; delete the exemption.");
        }

        foreach (string exemption in NonEndpointProtocolCode.Keys)
        {
            var files = AllInScopeFiles().Where(file => IsUnder(file, exemption)).ToArray();
            files.ShouldNotBeEmpty($"{exemption} matches no scanned file.");
            files.Where(file => EnvelopeSourceScan.MapRouteLiterals(ReadSource(file)).Count > 0)
                .ShouldBeEmpty($"{exemption} must not map routes; endpoint files are exempt only by derivation.");
            files.SelectMany(file => EnvelopeSourceScan.FindViolations(ReadSource(file)))
                .ShouldNotBeEmpty($"{exemption} exempts nothing; delete the exemption.");
        }
    }

    [Fact]
    public void Scan_IgnoresCommentsAndStringLiterals()
    {
        const string source = """"
            // return Results.NotFound();
            /* Results.Problem(statusCode: 500) */
            /// <see cref="Results.BadRequest()" />
            var text = "Results.NotFound()";
            var verbatim = @"new ProblemDetails ""quoted""";
            var raw = """throw new BadHttpRequestException("x")""";
            var interpolated = $"{(flag ? "Results.Conflict()" : "")} Results.Unauthorized()";
            var quote = '"';
            return ProblemResults.Problem(404, ProblemCodes.ReferenceResourceNotFound);
            """";

        EnvelopeSourceScan.FindViolations(source).ShouldBeEmpty();
    }

    [Fact]
    public void Scan_FlagsEveryForbiddenFormOnItsLine()
    {
        const string source = """
            using static Microsoft.AspNetCore.Http.Results;
            return Results.NotFound();
            return TypedResults . Problem<Foo> (detail);
            return Results.StatusCode(StatusCodes.Status409Conflict);
            return Results.StatusCode(statusCode);
            return Results.Json(body, statusCode: 503);
            context.Response.StatusCode = 404;
            context.Response.StatusCode = status;
            var problem = new HttpValidationProblemDetails();
            throw new BadHttpRequestException("Invalid id.");
            return result.Match(Results.Ok, Results.NotFound);
            return TypedResults.NotFound<Page<Title>>(page);
            return Results.Json<IReadOnlyList<Title>>(items, statusCode: 404);
            return Results.Json(body, options, "application/json", StatusCodes.Status409Conflict);
            return Results.Challenge();
            using Http = Microsoft.AspNetCore.Http.Results;
            response.StatusCode = 404;
            """;

        EnvelopeSourceScan.FindViolations(source).Select(violation => (violation.Line, violation.Form)).ShouldBe(
        [
            (1, "using static Results"),
            (2, "Results.NotFound"),
            (3, "TypedResults.Problem"),
            (4, "Results.StatusCode"),
            (5, "Results.StatusCode"),
            (6, "Results.Json statusCode"),
            (7, ".StatusCode ="),
            (8, ".StatusCode ="),
            (9, "new HttpValidationProblemDetails"),
            (10, "throw BadHttpRequestException"),
            (11, "Results.NotFound"),
            (12, "TypedResults.NotFound"),
            (13, "Results.Json statusCode"),
            (14, "Results.Json statusCode"),
            (15, "Results.Challenge"),
            (16, "using Results alias"),
            (17, ".StatusCode =")
        ]);
    }

    [Fact]
    public void Scan_AllowsSuccessRedirectAndNotModifiedStatuses()
    {
        const string source = """
            return Results.StatusCode(304);
            return Results.StatusCode(StatusCodes.Status202Accepted);
            context.Response.StatusCode = StatusCodes.Status207MultiStatus;
            if (context.Response.StatusCode == 404) return;
            return Results.Json(body);
            return Results.File(stream, "application/octet-stream");
            return result.Match(Results.Ok, errors => errors.ToProblem());
            return Results.Json<Page<Dictionary<string, int>>>(page, options, "application/json", 201);
            return Results.Json(body, statusCode: StatusCodes.Status202Accepted);
            var factory = Results.Json;
            """;

        EnvelopeSourceScan.FindViolations(source).ShouldBeEmpty();
    }

    private static IEnumerable<string> AllInScopeFiles() =>
        Directory.GetFiles(FindRepositoryDirectory("src/Romd.Hosting"), "*.cs", SearchOption.AllDirectories)
            .Where(IsRepositorySourceFile)
            .Select(ToRepositoryRelativePath)
            .Concat(["src/Romd.Admin.Host/Program.cs", "src/Romd.Consumer.Host/Program.cs"])
            .Order(StringComparer.Ordinal);

    private static IEnumerable<string> ScannedFiles() =>
        AllInScopeFiles()
            .Where(file => !HelperOwners.Contains(file, StringComparer.Ordinal))
            .Where(file => !NonEndpointProtocolCode.Keys.Any(exemption => IsUnder(file, exemption)))
            .Where(file => !EnvelopeSourceScan.IsProtocolSurface(ReadSource(file)));

    private static bool IsUnder(string file, string exemption) =>
        exemption.EndsWith('/')
            ? file.StartsWith(exemption, StringComparison.Ordinal)
            : file == exemption;

    private static string ReadSource(string relativePath) => File.ReadAllText(FindRepositoryFile(relativePath));

    /// <summary>One form's pinned occurrences in a file and the slice that removes them.</summary>
    private sealed record PinnedForm(string Form, int Count, OwningSlice Owner);
}
