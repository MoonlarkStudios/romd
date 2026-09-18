using OpenIddict.Server;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Ids;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Contracts.Common.Serialization;
using Romd.Contracts.Management.Commands;
using Romd.Domain.Identity;
using Romd.Host.Authorization;
using Romd.Host.Configuration;
using Romd.Host.Hubs;
using Romd.Hosting.Realtime;
using Romd.Hosting.Serialization;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Jobs;
using Romd.Persistence.Identity;
using Romd.Persistence;
using Romd.Infrastructure.Realtime;
using Romd.Storage;

namespace Romd.Hosting;

public static class RomdHostRegistrationExtensions
{
    private const string CanonicalIntegerPattern = "^-?(?:0|[1-9]\\d*)$";
    private const string CanonicalUnsignedIntegerPattern = "^(?:0|[1-9]\\d*)$";

    // Cookie scheme that carries the interactive login session into the OpenIddict authorize endpoint.
    public const string InteractiveLoginScheme = "romd-login";

    private static readonly string[] DevelopmentAdminCorsOrigins = ["http://localhost:5173", "http://localhost:5137"];
    private static readonly string[] DevelopmentConsumerCorsOrigins = ["http://localhost:5174", "http://localhost:5138"];

    public static RomdOptions AddRomdOptions(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        Action<IHostEnvironment, RomdOptions>? validate = null)
    {
        var romdOptions = configuration
            .GetSection(RomdOptions.SectionName)
            .Get<RomdOptions>() ?? new RomdOptions();

        validate?.Invoke(environment, romdOptions);
        romdOptions.ValidateAllowedImportPaths();
        romdOptions.EnsureDirectoriesExist();

        services.AddSingleton<IRomdOptions>(romdOptions);

        return romdOptions;
    }

    public static WebApplicationBuilder AddRomdStorage(
        this WebApplicationBuilder builder,
        IRomdOptions romdOptions)
    {
        long maxUploadBytes = GetMaxUploadBytes(romdOptions);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = maxUploadBytes;
        });

        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = maxUploadBytes;
        });

        builder.Services.AddRomdContentAddressableStorage(romdOptions);

        return builder;
    }

    public static IServiceCollection AddRomdContentAddressableStorage(
        this IServiceCollection services,
        IRomdOptions romdOptions)
    {
        long maxUploadBytes = GetMaxUploadBytes(romdOptions);

        services.AddContentAddressableStorage(options =>
        {
            options.RootPath = Path.Combine(romdOptions.DataDirectory, "content");
            options.MaxContentSize = maxUploadBytes;
        });

        return services;
    }

    /// <summary>
    ///     Persists the ASP.NET Core data-protection keyring to the shared data directory under a stable
    ///     application name. The interactive login cookie and OpenIddict authorization/device codes are
    ///     protected with these keys; without persistence they are ephemeral in a container (no <c>$HOME</c>),
    ///     so every restart would invalidate in-flight logins, and the admin/consumer hosts would not share a
    ///     keyring. Both surfaces call this with the same data directory and application name to converge.
    /// </summary>
    public static IServiceCollection AddRomdDataProtection(
        this IServiceCollection services,
        IRomdOptions romdOptions)
    {
        string keyDirectory = Path.Combine(romdOptions.DataDirectory, "dp-keys");
        Directory.CreateDirectory(keyDirectory);

        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
            .SetApplicationName("romd");

        return services;
    }

    public static IServiceCollection AddRomdIdentity(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        services.AddIdentity<RomdUser, RomdIdentityRole>(RomdIdentityOptions.Configure)
            .AddEntityFrameworkStores<RomdDbContext>()
            .AddDefaultTokenProviders();

        return services;
    }

    public static IServiceCollection AddRomdConsumerIdentity(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.Configure<IdentityOptions>(RomdIdentityOptions.Configure);
        services.TryAddSingleton<IdentityErrorDescriber>();
        services.TryAddScoped<IPasswordHasher<RomdUser>, PasswordHasher<RomdUser>>();

        return services;
    }

    public static IServiceCollection AddRomdAuth(
        this IServiceCollection services,
        IRomdOptions romdOptions,
        IHostEnvironment environment,
        string audience,
        string? issuer,
        IReadOnlyCollection<string> allowedClientIds,
        bool enableDeviceFlow = false)
    {
        byte[] jwtKey = Encoding.UTF8.GetBytes(romdOptions.JwtSecret);
        services.AddSingleton(new RomdSurfaceAuthOptions
        {
            Audience = audience,
            AllowedClientIds = allowedClientIds.ToHashSet(StringComparer.Ordinal)
        });

        services.AddRomdOpenIddict(
            environment,
            CreateOpenIddictProtectionKey(jwtKey),
            ResolveOpenIddictSigningKey(romdOptions),
            audience,
            ResolveIssuer(issuer),
            enableDeviceFlow);

        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
            })
            .AddCookie(InteractiveLoginScheme, options =>
            {
                options.Cookie.Name = "romd_login";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = environment.IsProduction()
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(15);
                options.SlidingExpiration = false;
            });

        services.AddSingleton<IAuthorizationHandler, MinimumRoleHandler>();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthorizationPolicies.RequireUser,
                policy => ConfigureRomdBearerPolicy(policy, RomdRoleType.User));
            options.AddPolicy(AuthorizationPolicies.RequireContributor,
                policy => ConfigureRomdBearerPolicy(policy, RomdRoleType.Contributor));
            options.AddPolicy(AuthorizationPolicies.RequireManager,
                policy => ConfigureRomdBearerPolicy(policy, RomdRoleType.Manager));
            options.AddPolicy(AuthorizationPolicies.RequireAdmin,
                policy => ConfigureRomdBearerPolicy(policy, RomdRoleType.Admin));
        });

        return services;
    }

    private static IServiceCollection AddRomdOpenIddict(
        this IServiceCollection services,
        IHostEnvironment environment,
        byte[] tokenProtectionKey,
        SecurityKey signingKey,
        string audience,
        Uri? issuer,
        bool enableDeviceFlow)
    {
        services.AddOpenIddict()
            .AddServer(options =>
            {
                // Behind a reverse proxy the issuer (and every URL in discovery metadata + the device-flow
                // verification URI) must be the public origin, not the internal container origin. Set it
                // explicitly rather than relying solely on the forwarded Host header through the proxy chain.
                if (issuer is not null)
                {
                    options.SetIssuer(issuer);
                }

                options.SetTokenEndpointUris("/connect/token")
                    .SetAuthorizationEndpointUris("/connect/authorize")
                    .SetEndSessionEndpointUris("/connect/logout")
                    .SetRevocationEndpointUris("/connect/revocation");

                options.AllowAuthorizationCodeFlow()
                    .RequireProofKeyForCodeExchange()
                    .AllowRefreshTokenFlow();

                if (enableDeviceFlow)
                {
                    options.SetDeviceAuthorizationEndpointUris("/connect/device")
                        .SetEndUserVerificationEndpointUris("/connect/verify")
                        .AllowDeviceAuthorizationFlow();
                }

                options.RegisterScopes(
                    OpenIddictConstants.Scopes.Email,
                    OpenIddictConstants.Scopes.OfflineAccess,
                    OpenIddictConstants.Scopes.Profile,
                    OpenIddictConstants.Scopes.Roles);

                options.AddEventHandler<OpenIddictServerEvents.HandleRevocationRequestContext>(builder =>
                    builder.UseScopedHandler<RevokeAuthenticationSession>()
                        .SetOrder(OpenIddictServerHandlers.Revocation.RevokeToken.Descriptor.Order + 1000));
                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(15));
                options.SetRefreshTokenLifetime(TimeSpan.FromDays(30));
                options.UseReferenceRefreshTokens();

                options.AddEncryptionKey(new SymmetricSecurityKey(tokenProtectionKey));
                options.AddSigningKey(signingKey);

                var aspNetCoreOptions = options.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableStatusCodePagesIntegration();
                if (enableDeviceFlow)
                {
                    aspNetCoreOptions.EnableEndUserVerificationEndpointPassthrough();
                }

                if (!environment.IsProduction())
                {
                    aspNetCoreOptions.DisableTransportSecurityRequirement();
                }
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                // Access tokens are self-contained JWE; token-entry validation makes them revocable
                // before their 15-minute expiry at the cost of an indexed token lookup per call.
                options.EnableTokenEntryValidation();
                options.EnableAuthorizationEntryValidation();
                options.AddEventHandler<OpenIddict.Validation.OpenIddictValidationEvents.ValidateTokenContext>(builder =>
                    builder.UseScopedHandler<ValidateAccountToken>()
                        .SetOrder(OpenIddict.Validation.OpenIddictValidationHandlers.Protection.ValidateTokenEntry.Descriptor.Order + 1_000));
                // Per-host audience is the boundary between admin and consumer tokens (shared keys/DB).
                options.AddAudiences(audience);
                // Query-string access tokens are accepted only for SignalR /hubs via the JwtBearer
                // handler; disable global query extraction so they are never honored elsewhere.
                options.UseAspNetCore().DisableAccessTokenExtractionFromQueryString();
            });

        return services;
    }

    private static byte[] CreateOpenIddictProtectionKey(byte[] jwtKey) =>
        SHA256.HashData(jwtKey);

    private static Uri? ResolveIssuer(string? issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer))
        {
            return null;
        }

        if (!Uri.TryCreate(issuer, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidOperationException(
                $"Configured public URL '{issuer}' is not an absolute URI. Set Romd:AdminHost:PublicUrl / "
                + "Romd:ConsumerHost:PublicUrl to an absolute origin such as https://console.example.com.");
        }

        return uri;
    }

    // OpenIddict requires an asymmetric signing key; an ephemeral key would invalidate every issued
    // token on restart and could not be shared across instances. Whichever host starts first creates
    // the persisted RSA key; creation is atomic (temp file + rename) so concurrent hosts converge on
    // a single key. This avoids a cold-start crash when an API host starts before the worker.
    private static SecurityKey ResolveOpenIddictSigningKey(IRomdOptions romdOptions)
    {
        OpenIddictSigningKey.EnsureCreated(romdOptions.DataDirectory);
        return OpenIddictSigningKey.Load(romdOptions.DataDirectory);
    }

    private static void ConfigureRomdBearerPolicy(
        AuthorizationPolicyBuilder policy,
        RomdRoleType minimumRole)
    {
        // All tokens are issued by OpenIddict (auth-code + device-flow) and validated locally; the
        // access tokens are JWE-encrypted so only the OpenIddict validation scheme can read them.
        policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        policy.AddRequirements(new MinimumRoleRequirement(minimumRole));
    }

    public static IServiceCollection AddRomdAdminAuth(
        this IServiceCollection services,
        IRomdOptions romdOptions,
        IConfiguration? configuration,
        IHostEnvironment environment)
    {
        var options = RomdHostSurfaceOptions.FromConfiguration(
            configuration,
            RomdHostSurfaceOptions.AdminSectionName,
            environment,
            DevelopmentAdminCorsOrigins);

        return services.AddRomdAuth(
            romdOptions,
            environment,
            options.JwtAudience ?? RomdHostSurfaceOptions.DefaultAdminAudience,
            options.PublicUrl,
            allowedClientIds: [RomdOpenIddictClients.AdminSpa]);
    }

    public static IServiceCollection AddRomdConsumerAuth(
        this IServiceCollection services,
        IRomdOptions romdOptions,
        IConfiguration? configuration,
        IHostEnvironment environment)
    {
        var options = RomdHostSurfaceOptions.FromConfiguration(
            configuration,
            RomdHostSurfaceOptions.ConsumerSectionName,
            environment,
            DevelopmentConsumerCorsOrigins);

        return services.AddRomdAuth(
            romdOptions,
            environment,
            options.JwtAudience ?? RomdHostSurfaceOptions.DefaultConsumerAudience,
            options.PublicUrl,
            allowedClientIds: [RomdOpenIddictClients.ConsumerSpa, RomdOpenIddictClients.Console],
            enableDeviceFlow: true);
    }

    public static IServiceCollection AddRomdAdminHttp(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        IHostEnvironment? environment = null,
        string openApiDocumentName = "v1")
    {
        // Admin contract enums serialize as named strings, never integers
        // (docs/decisions/admin-api-contract-policy.md, "Opaque Public Identity"). Reading is
        // strict too: only exact ordinal declared names bind; integers, case variants,
        // comma-composed values, and unknown names fail. Registered in the admin composition
        // only; consumer host JSON options are untouched.
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
            options.SerializerOptions.Converters.Add(new StrictJsonStringEnumConverter());
        });

        // Binding failures (malformed JSON bodies, invalid enum names, unparsable parameters)
        // must yield a deterministic 400 envelope in every environment, not the framework's
        // bodyless 400 outside Development. Throwing routes them through the admin exception
        // handler, which preserves the BadHttpRequestException status code.
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

        // Unhandled exceptions on the admin surface become sanitized ProblemDetails envelopes
        // (docs/decisions/admin-api-contract-policy.md, "One Error Envelope"): generic title, no
        // exception message or stack, plus the errorCode/traceId extensions. Endpoint-produced
        // problems already carry errorCode, so TryAdd only fills the exception-handler path.
        // Request-binding failures get a request-scoped errorCode instead of the unhandled one.
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var exception = context.HttpContext.Features
                .Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            context.ProblemDetails.Extensions.TryAdd(
                Romd.Host.Endpoints.ProblemResults.ErrorCodeExtensionName,
                exception switch
                {
                    BadHttpRequestException => "Request.InvalidBody",
                    Romd.Admin.Application.Common.Persistence.PersistenceConflictException => "Catalog.TitleConflict",
                    _ => "General.Unhandled"
                });
            context.ProblemDetails.Extensions.TryAdd(
                Romd.Host.Endpoints.ProblemResults.TraceIdExtensionName,
                System.Diagnostics.Activity.Current?.Id ?? context.HttpContext.TraceIdentifier);
        });

        services.AddRomdCors(
            RomdCorsPolicyNames.Admin,
            RomdHostSurfaceOptions.AdminSectionName,
            configuration,
            environment,
            DevelopmentAdminCorsOrigins);
        services.AddOpenApi(openApiDocumentName, options =>
        {
            options.AddSchemaTransformer(ApplyOpaquePublicIdSchemaAsync);
            options.AddSchemaTransformer(ApplyReferencePatchSchemaAsync);
            options.AddSchemaTransformer(ApplyNumericWireSchemaAsync);
            options.AddSchemaTransformer(ApplyManagementCollectionSchemaAsync);
            options.AddSchemaTransformer(ApplyStrictEnumSchemaAsync);
            options.AddDocumentTransformer((document, _, _) =>
            {
                ApplyAdminOpenApiSchemaFixes(document);
                ApplyAdminOpenApiSecurityScheme(document);
                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, context, _) =>
            {
                ApplyAdminOpenApiOperationSecurity(operation, context);
                return Task.CompletedTask;
            });
        });

        return services;
    }

    // Single OAuth2 scheme: admin access tokens are JWE-encrypted OpenIddict tokens obtained via
    // the authorization-code + PKCE flow, so publishing a generic raw http-bearer scheme would be
    // misleading. Relative URLs keep the document valid for build-time generation.
    private const string AdminOAuth2SecuritySchemeId = "oauth2";

    // Exactly the intersection of the scopes OpenIddict registers (AddRomdOpenIddict) and the
    // scopes the admin SPA requests (web/packages/romd-admin-app/src/auth/userManager.ts).
    private static readonly IReadOnlyDictionary<string, string> AdminOAuth2Scopes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OpenIddictConstants.Scopes.Email] = "Read the signed-in user's email address.",
            [OpenIddictConstants.Scopes.OfflineAccess] = "Obtain refresh tokens.",
            [OpenIddictConstants.Scopes.Profile] = "Read the signed-in user's profile.",
            [OpenIddictConstants.Scopes.Roles] = "Read the signed-in user's roles."
        };

    private static void ApplyAdminOpenApiSecurityScheme(OpenApiDocument document)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??=
            new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes[AdminOAuth2SecuritySchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Description = "OpenIddict authorization-code flow with PKCE. Access tokens are presented as bearer tokens.",
            Flows = new OpenApiOAuthFlows
            {
                AuthorizationCode = new OpenApiOAuthFlow
                {
                    AuthorizationUrl = new Uri("/connect/authorize", UriKind.Relative),
                    TokenUrl = new Uri("/connect/token", UriKind.Relative),
                    Scopes = AdminOAuth2Scopes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                }
            }
        };

        // Document-level default: every operation requires the OAuth2 scheme unless it opts out
        // with an explicit empty per-operation requirement (see the operation transformer).
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(AdminOAuth2SecuritySchemeId, document)] = new List<string>()
        });
    }

    private static void ApplyAdminOpenApiOperationSecurity(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context)
    {
        bool allowsAnonymous = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<IAllowAnonymous>()
            .Any();

        // Anonymous operations override the document-level default with an explicit empty
        // `security: []`; protected operations inherit the document default untouched.
        if (allowsAnonymous)
        {
            operation.Security = new List<OpenApiSecurityRequirement>();
        }
    }

    private static void ApplyAdminOpenApiSchemaFixes(OpenApiDocument document)
    {
        var schemas = document.Components?.Schemas;
        if (schemas is null || !schemas.TryGetValue("LibraryDto", out var librarySchema))
            return;

        var properties = librarySchema.Properties;
        if (properties is null || !properties.ContainsKey("configuration"))
            return;

        properties["configuration"] = new OpenApiSchema
        {
            OneOf =
            [
                new OpenApiSchema { Type = JsonSchemaType.Null },
                new OpenApiSchemaReference("LibraryConfigurationDto", document, null)
            ]
        };
    }

    private static async Task ApplyStrictEnumSchemaAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        Type serializedType = context.JsonTypeInfo.Type;
        Type? enumType = Nullable.GetUnderlyingType(serializedType);
        bool isNullable = enumType is not null;
        enumType ??= serializedType;

        if (enumType.IsEnum)
        {
            schema.Enum = Enum.GetNames(enumType)
                .Select(name => (JsonNode)JsonValue.Create(name)!)
                .ToList();
            if (isNullable)
                schema.Enum.Add(null!);
            return;
        }

        Type? elementType = context.JsonTypeInfo.ElementType;
        if (elementType?.IsEnum == true)
        {
            schema.Items = await context.GetOrCreateSchemaAsync(
                elementType,
                parameterDescription: null,
                cancellationToken);
        }
    }

    private static Task ApplyReferencePatchSchemaAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken ct)
    {
        if (!typeof(ReferencePatchDto).IsAssignableFrom(context.JsonTypeInfo.Type)) return Task.CompletedTask;
        schema.Type = JsonSchemaType.Object;
        schema.Required = new HashSet<string>();
        schema.AdditionalPropertiesAllowed = false;
        schema.Properties = ReferencePatchJsonConverter.EditableProperties(context.JsonTypeInfo.Type)
            .ToDictionary(property => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(property.Name), property => (IOpenApiSchema)new OpenApiSchema
            {
                Type = (property.PropertyType == typeof(IReadOnlyList<string>) ? JsonSchemaType.Array
                    : property.PropertyType == typeof(bool?) ? JsonSchemaType.Boolean
                    : property.PropertyType == typeof(int?) ? JsonSchemaType.Integer : JsonSchemaType.String)
                    | (property.Name is "Description" or "Icon" ? JsonSchemaType.Null : 0),
                Items = property.PropertyType == typeof(IReadOnlyList<string>) ? new OpenApiSchema { Type = JsonSchemaType.String } : null
            });
        return Task.CompletedTask;
    }

    private static Task ApplyOpaquePublicIdSchemaAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        Type serializedType = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ??
                              context.JsonTypeInfo.Type;
        if (serializedType != typeof(Sqid))
            return Task.CompletedTask;

        schema.Type = JsonSchemaType.String;
        schema.Properties = null;
        schema.Required = null;
        schema.Format = null;
        schema.Pattern = null;
        return Task.CompletedTask;
    }

    private static Task ApplyNumericWireSchemaAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        Type serializedType = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ??
                              context.JsonTypeInfo.Type;
        if (serializedType == typeof(ByteCount))
        {
            schema.Type = JsonSchemaType.String;
            schema.Properties = null;
            schema.Required = null;
            schema.Format = null;
            schema.Pattern = CanonicalUnsignedIntegerPattern;
            return Task.CompletedTask;
        }

        var converter = context.JsonPropertyInfo?.AttributeProvider?
            .GetCustomAttributes(typeof(JsonConverterAttribute), inherit: true)
            .OfType<JsonConverterAttribute>()
            .SingleOrDefault();
        if (converter?.ConverterType != typeof(CanonicalInt64StringJsonConverter))
            return Task.CompletedTask;

        bool isNullable = Nullable.GetUnderlyingType(context.JsonPropertyInfo!.PropertyType) is not null;
        schema.Type = isNullable
            ? JsonSchemaType.String | JsonSchemaType.Null
            : JsonSchemaType.String;
        schema.Format = null;
        Type valueType = Nullable.GetUnderlyingType(context.JsonPropertyInfo.PropertyType) ??
                         context.JsonPropertyInfo.PropertyType;
        schema.Pattern = valueType == typeof(ulong)
            ? CanonicalUnsignedIntegerPattern
            : CanonicalIntegerPattern;
        return Task.CompletedTask;
    }

    private static Task ApplyManagementCollectionSchemaAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.JsonPropertyInfo?.AttributeProvider is not PropertyInfo property ||
            property.DeclaringType != typeof(BatchDeleteRomsRequest) ||
            property.Name != nameof(BatchDeleteRomsRequest.RomIds))
        {
            return Task.CompletedTask;
        }

        schema.MinItems = BatchDeleteRomsRequest.MinimumRomIds;
        schema.MaxItems = BatchDeleteRomsRequest.MaximumRomIds;
        schema.UniqueItems = true;
        return Task.CompletedTask;
    }

    public static IServiceCollection AddRomdConsumerHttp(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        IHostEnvironment? environment = null,
        string openApiDocumentName = "v1")
    {
        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);

        services.AddRomdCors(
            RomdCorsPolicyNames.Consumer,
            RomdHostSurfaceOptions.ConsumerSectionName,
            configuration,
            environment,
            DevelopmentConsumerCorsOrigins);
        services.AddOpenApi(openApiDocumentName, options =>
        {
            options.AddSchemaTransformer(ApplyOpaquePublicIdSchemaAsync);
            options.AddSchemaTransformer(ApplyReferencePatchSchemaAsync);
            options.AddSchemaTransformer(ApplyNumericWireSchemaAsync);
        });

        return services;
    }

    public static IServiceCollection AddRomdRealtimeTransport(this IServiceCollection services)
    {
        services.AddSignalR();

        return services;
    }

    public static IServiceCollection AddRomdAdminRealtimeOutboxRelay(this IServiceCollection services)
    {
        services.AddScoped<SignalRJobNotifier>();
        services.AddScoped<SignalRStatsNotifier>();
        services.AddScoped<IAdminRealtimeEventSink, SignalRAdminRealtimeEventSink>();
        services.AddHostedService<AdminRealtimeOutboxDispatcher>();

        return services;
    }

    public static IServiceCollection AddRomdHangfireClient(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        bool useJobStateSyncFilter = false)
    {
        if (environment.IsEnvironment("Testing"))
        {
            return services;
        }

        services.AddHangfire((serviceProvider, config) =>
        {
            string connectionString = HangfirePostgreSqlConfiguration.GetRequiredConnectionString(
                configuration,
                HangfirePostgreSqlConfiguration.RuntimeConnectionName);
            var storageOptions = HangfirePostgreSqlConfiguration.CreateStorageOptions(prepareSchema: false);

            config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings();

            if (useJobStateSyncFilter)
            {
                config.UseFilter(serviceProvider.GetRequiredService<HangfireJobStateSyncFilter>());
            }

            config.UsePostgreSqlStorage(
                options => options.UseNpgsqlConnection(connectionString),
                storageOptions);
        });

        return services;
    }

    public static IServiceCollection AddRomdSchemaProvisioning(
        this IServiceCollection services,
        IHostEnvironment environment)
    {
        if (!environment.IsEnvironment("Testing"))
        {
            services.AddHostedService<HangfireSchemaProvisioner>();
            services.AddHostedService<PostgreSqlSchemaProvisioner>();
        }

        return services;
    }

    public static IServiceCollection AddRomdHangfireServer(
        this IServiceCollection services,
        IHostEnvironment environment)
    {
        if (environment.IsEnvironment("Testing"))
        {
            return services;
        }

        services.AddHangfireServer(options =>
        {
            options.Queues = ["default"];
            options.WorkerCount = Environment.ProcessorCount;
        });

        // #193 QueueSafety_ReplaceJobsSharingSource proves overlapping replacement ingestion
        // fails one attempt at IX_DatFiles_Pending_SourceId; serial ingest/activation succeeds.
        // Keep uploads serialized until replacements coordinate by source across both phases.
        services.AddHangfireServer(options =>
        {
            options.ServerName = "upload-worker";
            options.Queues = ["upload"];
            options.WorkerCount = 1;
        });

        services.AddHangfireServer(options =>
        {
            options.ServerName = "enrichment-worker";
            options.Queues = ["enrichment"];
            // #193 QueueSafety_SingleAndBulkEnrichmentShareTitle reproduces a provider-layer
            // unique-key collision. Distinct job fences do not serialize title/evidence writes.
            options.WorkerCount = 1;
        });

        services.AddHangfireServer(options =>
        {
            options.ServerName = "materialization-worker";
            options.Queues = ["materialization"];
            // #193 QueueSafety_Materialization tests cover shared catalogs, per-library
            // projection publication, cancellation handoff and reflagging. Two workers improved
            // measured warm rebuild throughput; the active-library index and fences stay required.
            options.WorkerCount = 2;
        });

        return services;
    }

    private static IServiceCollection AddRomdCors(
        this IServiceCollection services,
        string policyName,
        string sectionName,
        IConfiguration? configuration,
        IHostEnvironment? environment,
        IReadOnlyCollection<string> developmentCorsOrigins)
    {
        var corsEnvironment = environment ?? new RomdCorsDefaultEnvironment();
        var options = RomdHostSurfaceOptions.FromConfiguration(
            configuration,
            sectionName,
            corsEnvironment,
            developmentCorsOrigins);

        services.AddCors(cors => cors.AddPolicy(policyName, policy =>
        {
            ConfigureCorsPolicy(policy, options);
        }));

        return services;
    }

    private static void ConfigureCorsPolicy(CorsPolicyBuilder policy, RomdHostSurfaceOptions options)
    {
        if (options.CorsOrigins.Length > 0)
        {
            policy.WithOrigins(options.CorsOrigins);
        }

        policy
            .AllowAnyMethod()
            .AllowAnyHeader()
            .WithExposedHeaders("ETag", "Location");

        if (options.AllowCorsCredentials)
        {
            policy.AllowCredentials();
        }
    }

    private static long GetMaxUploadBytes(IRomdOptions romdOptions) =>
        romdOptions.MaxUploadBytes > 0
            ? romdOptions.MaxUploadBytes
            : 10L * 1024 * 1024 * 1024;

    private sealed class RomdCorsDefaultEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Romd";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
