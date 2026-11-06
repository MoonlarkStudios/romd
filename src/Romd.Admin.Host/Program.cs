using Romd.Host.Configuration;
using Romd.Host.Endpoints;
using Romd.Hosting;
using Romd.Hosting.Security;
using Romd.Infrastructure;
using Romd.Storage;
using Serilog;
using EnrichmentOptions = Romd.Admin.Application.Configuration.EnrichmentOptions;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting ROMD admin host");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.With<RomdLogRedactionEnricher>()
        .WriteTo.Console());

    var romdOptions = builder.Services.AddRomdOptions(
        builder.Configuration,
        builder.Environment,
        ProductionSecretValidator.Validate);
    builder.AddRomdStorage(romdOptions);

    builder.Services.Configure<EnrichmentOptions>(
        builder.Configuration.GetSection(EnrichmentOptions.SectionName));

    builder.Services
        .AddRomdForwardedHeaders(builder.Configuration)
        .AddRomdDataProtection(romdOptions)
        .AddRomdAdminEnqueueInfrastructure(builder.Configuration)
        .AddRomdRealtimeTransport()
        .AddRomdAdminRealtimeOutboxRelay()
        .AddRomdIdentity()
        .AddRomdAdminAuth(romdOptions, builder.Configuration, builder.Environment)
        .AddRomdHangfireClient(builder.Configuration, builder.Environment)
        .AddRomdAdminHttp(builder.Configuration, builder.Environment, "admin-v1")
        .AddRomdReadinessSignal(builder.Configuration);

    var app = builder.Build();

    // Must run first so the proxy's X-Forwarded-Proto/Host are applied before request logging and OpenIddict.
    app.UseRomdForwardedHeaders();

    // Request logging wraps the exception handler, so it records the final enveloped status
    // instead of logging every handled exception a second time.
    app.UseSerilogRequestLogging();

    // Unhandled exceptions become sanitized ProblemDetails envelopes (no exception details).
    app.UseRomdExceptionHandling();

    app.UseRomdHubQueryStringToken();
    app.UseRomdInteractiveAuthenticationForms();
    app.UseAuthentication();
    app.UseCors(RomdCorsPolicyNames.Admin);
    app.UseAuthorization();

    // Unmatched /api routes and routing's 405/415 rejections answer with the shared envelope.
    app.UseRomdApiRoutingProblems();
    app.UseRomdQueryNameValidation();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/openapi/admin-v1.json", "ROMD Admin API v1");
        });
    }

    app.MapRomdRealtimeHubs();
    app.MapRomdHealthCheck();
    app.MapRomdReadinessCheck();
    app.MapRomdOpenIddictEndpoints();
    app.MapGroup("/api").MapRomdAdminHttp();
    app.MapArtworkDeliveryEndpoints();
    app.MapStorageEndpoints();

    app.UseStaticFiles();
    // The SPA's client routes (web/packages/romd-admin-app/src/routes.tsx), scoped to their
    // prefixes like the consumer host's. A catch-all would also be a routing candidate for /api,
    // which turns a wrong method into a 404 instead of the 405 envelope.
    string[] adminSpaRoutes =
    [
        "account", "activate", "audit", "auth", "catalog", "collections", "diagnostics", "disc-layouts",
        "enrichment", "import", "inbox", "integrations", "jobs", "libraries", "login", "reference-data",
        "registry", "roms", "search", "settings", "storage", "systems", "taxonomy", "titles", "users"
    ];
    app.MapFallbackToFile("/", "index.html");
    foreach (string route in adminSpaRoutes)
        app.MapFallbackToFile($"{route}/{{*path:nonfile}}", "index.html");

    await app.RunRomdServerAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

namespace Romd.Admin.Host
{
    public partial class Program;
}
