using Microsoft.AspNetCore.StaticFiles;
using Romd.Consumer.Host.Testing;
using Romd.Host.Configuration;
using Romd.Host.Endpoints;
using Romd.Hosting;
using Romd.Hosting.Security;
using Romd.Hosting.Vfs;
using Romd.Infrastructure;
using Romd.Storage;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting ROMD consumer host");

    var builder = WebApplication.CreateBuilder(args);

    await ConsumerHostTestStartupGate.WaitIfRequestedAsync(builder.Environment);

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

    builder.Services.AddRomdConsumerVfs(builder.Configuration);
    builder.Services
        .AddRomdForwardedHeaders(builder.Configuration)
        .AddRomdBrowserPlayback(builder.Configuration)
        .AddRomdDataProtection(romdOptions)
        .AddRomdConsumerInfrastructure(builder.Configuration)
        .AddRomdConsumerIdentity()
        .AddRomdConsumerAuth(romdOptions, builder.Configuration, builder.Environment)
        .AddRomdConsumerHttp(builder.Configuration, builder.Environment, "consumer-v1")
        .AddRomdReadinessSignal(builder.Configuration);

    var app = builder.Build();

    // Must run first so the proxy's X-Forwarded-Proto/Host are applied before request logging and OpenIddict.
    app.UseRomdForwardedHeaders();

    // Request logging wraps the exception handler, so it records the final enveloped status
    // instead of logging every handled exception a second time.
    app.UseSerilogRequestLogging();

    // Unhandled exceptions become sanitized ProblemDetails envelopes (no exception details).
    app.UseRomdExceptionHandling();

    app.UseRomdInteractiveAuthenticationForms();
    app.UseAuthentication();
    app.UseCors(RomdCorsPolicyNames.Consumer);
    app.UseAuthorization();

    // Unmatched /api routes and routing's 405/415 rejections answer with the shared envelope.
    app.UseRomdApiRoutingProblems();
    app.UseRomdQueryNameValidation();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/openapi/consumer-v1.json", "ROMD Consumer API v1");
        });
    }

    app.MapRomdHealthCheck();
    app.MapRomdReadinessCheck();
    app.MapRomdOpenIddictEndpoints();
    app.MapGroup("/api").MapRomdConsumerHttp();
    app.MapArtworkDeliveryEndpoints();
    app.MapConsumerStorageEndpoints();
    app.MapConsumerVfsEndpoints();

    // EmulatorJS ships its core packages as `.data` files, which aren't in ASP.NET's default
    // content-type map and would otherwise 404. Map the extensions browser playback fetches.
    var staticContentTypes = new FileExtensionContentTypeProvider();
    staticContentTypes.Mappings[".data"] = "application/octet-stream";
    staticContentTypes.Mappings[".wasm"] = "application/wasm";
    app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = staticContentTypes });
    app.MapFallbackToFile("/", "index.html");
    app.MapFallbackToFile("login", "index.html");
    app.MapFallbackToFile("auth/callback", "index.html");
    app.MapFallbackToFile("library", "index.html");
    app.MapFallbackToFile("titles/{*path:nonfile}", "index.html");
    app.MapFallbackToFile("collections/{*path:nonfile}", "index.html");

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

namespace Romd.Consumer.Host
{
    public partial class Program;
}
