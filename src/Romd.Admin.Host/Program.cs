using Romd.Host.Configuration;
using Romd.Host.Endpoints;
using Romd.Hosting;
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

    // Unhandled exceptions become sanitized ProblemDetails envelopes (no exception details).
    app.UseRomdAdminExceptionHandling();

    app.UseSerilogRequestLogging();

    app.UseRomdHubQueryStringToken();
    app.UseAuthentication();
    app.UseCors(RomdCorsPolicyNames.Admin);
    app.UseAuthorization();

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

    app.MapFallback("/api/{**path}", () => Results.NotFound());
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");

    app.Run();
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
