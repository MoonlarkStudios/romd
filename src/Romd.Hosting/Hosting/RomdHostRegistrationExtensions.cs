using System.ComponentModel.DataAnnotations;
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
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Ids;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Contracts.Common.Serialization;
using Romd.Contracts.Consumer.Account;
using Romd.Contracts.Management.Commands;
using Romd.Contracts.Management.Export;
using Romd.Domain.Identity;
using Romd.Host.Authorization;
using Romd.Host.Configuration;
using Romd.Host.Endpoints;
using Romd.Host.Hubs;
using Romd.Hosting.Dashboard;
using Romd.Hosting.Realtime;
using Romd.Hosting.Security;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Realtime;
using Romd.Persistence;
using Romd.Persistence.Identity;
using Romd.Storage;

namespace Romd.Hosting;

public static class RomdHostRegistrationExtensions
{
    private const string _canonicalIntegerPattern = "^-?(?:0|[1-9]\\d*)$";
    private const string _canonicalUnsignedIntegerPattern = "^(?:0|[1-9]\\d*)$";

    // Cookie scheme that carries the interactive login session into the OpenIddict authorize endpoint.
    public const string InteractiveLoginScheme = "romd-login";

    private static readonly string[] _developmentAdminCorsOrigins = ["http://localhost:5173", "http://localhost:5137"];
    private static readonly string[] _developmentConsumerCorsOrigins = ["http://localhost:5174", "http://localhost:5138"];

    public static RomdOptions AddRomdOptions(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        Action<IHostEnvironment, RomdOptions, IConfiguration>? validate = null)
    {
        var romdOptions = configuration
            .GetSection(RomdOptions.SectionName)
            .Get<RomdOptions>() ?? new RomdOptions();

        // Binding directly does not run data annotations. Validate before creating directories
        // or deriving key material, including when a host uses the default Production environment.
        Validator.ValidateObject(romdOptions, new ValidationContext(romdOptions), validateAllProperties: true);
        validate?.Invoke(environment, romdOptions, configuration);
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

        // Multipart uploads buffer under ASPNETCORE_TEMP, which ASP.NET Core requires to exist.
        if (Environment.GetEnvironmentVariable("ASPNETCORE_TEMP") is { Length: > 0 } requestBuffer)
            Directory.CreateDirectory(requestBuffer);

        // Kestrel keeps its default body limit; upload routes raise it with endpoint metadata.
        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = maxUploadBytes;
        });

        builder.Services.AddRomdContentAddressableStorage(romdOptions, builder.Configuration);

        return builder;
    }

    public static IServiceCollection AddRomdContentAddressableStorage(
        this IServiceCollection services,
        IRomdOptions romdOptions,
        IConfiguration configuration)
    {
        long maxUploadBytes = GetMaxUploadBytes(romdOptions);
        var storage = new ContentStoreOptions
        {
            RootPath = Path.Combine(romdOptions.DataDirectory, "content"),
            MaxContentSize = RomdStorageConfiguration.DefaultMaxContentSize
        };
        RomdStorageConfiguration.Apply(storage, configuration);
        // The worker stores uploads, so a larger upload limit would accept files it must then reject.
        if (maxUploadBytes > storage.MaxContentSize)
            throw new InvalidOperationException(
                $"Romd:MaxUploadBytes ({maxUploadBytes}) must not exceed {RomdStorageConfiguration.SectionName}:MaxContentSize ({storage.MaxContentSize}).");

        services.AddContentAddressableStorage(options =>
        {
            options.RootPath = storage.RootPath;
            options.MaxContentSize = storage.MaxContentSize;
            options.WritePolicy = storage.WritePolicy;
            options.SeekableFrameSize = storage.SeekableFrameSize;
            options.SeekIndexCacheBytes = storage.SeekIndexCacheBytes;
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
            .AddPasswordValidator<RomdPasswordLengthValidator>()
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
        bool enableDeviceFlow = false,
        IConfiguration? configuration = null)
    {
        var attempts = configuration?.GetSection(AuthenticationAttemptOptions.SectionName).Get<AuthenticationAttemptOptions>()
            ?? new AuthenticationAttemptOptions();
        attempts.Validate();
        services.AddSingleton(attempts);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AuthenticationAttemptLimiter>();
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "romd_csrf";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = environment.IsProduction()
                ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });
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
            _developmentAdminCorsOrigins);

        return services.AddRomdAuth(
            romdOptions,
            environment,
            options.JwtAudience ?? RomdHostSurfaceOptions.DefaultAdminAudience,
            options.PublicUrl,
            allowedClientIds: [RomdOpenIddictClients.AdminSpa],
            configuration: configuration);
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
            _developmentConsumerCorsOrigins);

        return services.AddRomdAuth(
            romdOptions,
            environment,
            options.JwtAudience ?? RomdHostSurfaceOptions.DefaultConsumerAudience,
            options.PublicUrl,
            allowedClientIds: [RomdOpenIddictClients.ConsumerSpa, RomdOpenIddictClients.Console, RomdOpenIddictClients.Mount],
            enableDeviceFlow: true,
            configuration: configuration);
    }

    public static IServiceCollection AddRomdAdminHttp(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        IHostEnvironment? environment = null,
        string openApiDocumentName = "v1")
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<DashboardStatsCache>();
        // Lifecycle StartingAsync finishes ownership/recovery before any hosted service starts listening.
        services.AddHostedService(provider => new RequestBufferDirectoryLease(
            Environment.GetEnvironmentVariable("ASPNETCORE_TEMP"),
            provider.GetRequiredService<ILogger<RequestBufferDirectoryLease>>()));

        services.AddRomdHttpContract();

        services.AddRomdCors(
            RomdCorsPolicyNames.Admin,
            RomdHostSurfaceOptions.AdminSectionName,
            configuration,
            environment,
            _developmentAdminCorsOrigins);
        services.AddOpenApi(openApiDocumentName, options =>
        {
            options.AddSchemaTransformer(ApplyOpaquePublicIdSchemaAsync);
            options.AddSchemaTransformer(ApplyReferencePatchSchemaAsync);
            options.AddSchemaTransformer(ApplyReferenceRelationshipSchemaAsync);
            options.AddSchemaTransformer(ApplyNumericWireSchemaAsync);
            options.AddSchemaTransformer(ApplyDeliveryFormatSchemaAsync);
            options.AddSchemaTransformer(ApplyManagementCollectionSchemaAsync);
            options.AddSchemaTransformer(ApplyStrictEnumSchemaAsync);
            options.AddSchemaTransformer(ApplyProblemDetailsEnvelopeSchemaAsync);
            options.AddOperationTransformer(ApplyClosedValuesOperationSchemaAsync);
            options.AddOperationTransformer(ApplyDeliveryFormatOperationSchemaAsync);
            options.AddDocumentTransformer((document, _, _) =>
            {
                ApplyAdminOpenApiSchemaFixes(document);
                return Task.CompletedTask;
            });
            AddRomdOpenApiSecurity(options);
        });

        return services;
    }

    // Single OAuth2 scheme on both surfaces: access tokens are JWE-encrypted OpenIddict tokens
    // obtained via the authorization-code + PKCE flow, so publishing a generic raw http-bearer
    // scheme would be misleading. Relative URLs keep the document valid for build-time generation.
    private const string _oAuth2SecuritySchemeId = "oauth2";

    // Exactly the intersection of the scopes OpenIddict registers (AddRomdOpenIddict) and the
    // scopes the first-party clients request (web/packages/romd-admin-app/src/auth/userManager.ts,
    // web/packages/romd-consumer-app/src/auth/userManager.ts and the console's
    // clients/romd_console/lib/src/data/consumer_api_client.dart).
    private static readonly IReadOnlyDictionary<string, string> _oAuth2Scopes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OpenIddictConstants.Scopes.Email] = "Read the signed-in user's email address.",
            [OpenIddictConstants.Scopes.OfflineAccess] = "Obtain refresh tokens.",
            [OpenIddictConstants.Scopes.Profile] = "Read the signed-in user's profile.",
            [OpenIddictConstants.Scopes.Roles] = "Read the signed-in user's roles."
        };

    // Publishes the security model on either surface's document
    // (docs/decisions/api-contract-policy.md, "Protected By Default"): the OAuth2 scheme as the
    // document default and `security: []` on anonymous operations.
    private static void AddRomdOpenApiSecurity(OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            ApplyOpenApiSecurityScheme(document);
            return Task.CompletedTask;
        });
        options.AddOperationTransformer((operation, context, _) =>
        {
            ApplyOpenApiOperationSecurity(operation, context);
            return Task.CompletedTask;
        });
    }

    private static void ApplyOpenApiSecurityScheme(OpenApiDocument document)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??=
            new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes[_oAuth2SecuritySchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Description = "OpenIddict authorization-code flow with PKCE. Access tokens are presented as bearer tokens.",
            Flows = new OpenApiOAuthFlows
            {
                AuthorizationCode = new OpenApiOAuthFlow
                {
                    AuthorizationUrl = new Uri("/connect/authorize", UriKind.Relative),
                    TokenUrl = new Uri("/connect/token", UriKind.Relative),
                    Scopes = _oAuth2Scopes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                }
            }
        };

        // Document-level default: every operation requires the OAuth2 scheme unless it opts out
        // with an explicit empty per-operation requirement (see the operation transformer).
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(_oAuth2SecuritySchemeId, document)] = new List<string>()
        });
    }

    private static void ApplyOpenApiOperationSecurity(
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
        if (schemas is null || !schemas.TryGetValue("Library", out var librarySchema))
            return;

        var properties = librarySchema.Properties;
        if (properties is null || !properties.ContainsKey("configuration"))
            return;

        properties["configuration"] = new OpenApiSchema
        {
            OneOf =
            [
                new OpenApiSchema { Type = JsonSchemaType.Null },
                new OpenApiSchemaReference("LibraryConfiguration", document, null)
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

    // Publishes the envelope extensions on the shared ProblemDetails component
    // (docs/decisions/api-contract-policy.md, "One Error Envelope"). ProblemResults and the
    // exception handler always write errorCode and traceId; errors appears only for validation.
    private static Task ApplyProblemDetailsEnvelopeSchemaAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type != typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))
            return Task.CompletedTask;

        schema.Properties ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        schema.Properties[ProblemResults.ErrorCodeExtensionName] = new OpenApiSchema { Type = JsonSchemaType.String };
        schema.Properties[ProblemResults.ErrorsExtensionName] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            AdditionalProperties = new OpenApiSchema
            {
                Type = JsonSchemaType.Array,
                Items = new OpenApiSchema { Type = JsonSchemaType.String }
            }
        };
        schema.Properties[ProblemResults.TraceIdExtensionName] = new OpenApiSchema { Type = JsonSchemaType.String };
        schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
        schema.Required.Add(ProblemResults.ErrorCodeExtensionName);
        schema.Required.Add(ProblemResults.TraceIdExtensionName);
        return Task.CompletedTask;
    }

    private static readonly Dictionary<Type, (JsonSchemaType Type, string? Format)> _patchFieldSchemas = new()
    {
        [typeof(string)] = (JsonSchemaType.String, null),
        [typeof(bool?)] = (JsonSchemaType.Boolean, null),
        [typeof(int?)] = (JsonSchemaType.Integer, "int32"),
        [typeof(double?)] = (JsonSchemaType.Number, "double"),
        [typeof(DateOnly?)] = (JsonSchemaType.String, "date"),
        [typeof(IReadOnlyList<string>)] = (JsonSchemaType.Array, null)
    };

    private static Task ApplyReferencePatchSchemaAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken ct)
    {
        if (!typeof(ReferencePatch).IsAssignableFrom(context.JsonTypeInfo.Type))
            return Task.CompletedTask;
        var clearable = ((ReferencePatch)Activator.CreateInstance(context.JsonTypeInfo.Type)!).ClearableFields;
        schema.Type = JsonSchemaType.Object;
        schema.Required = new HashSet<string>();
        schema.AdditionalPropertiesAllowed = false;
        schema.Properties = ReferencePatchJsonConverter.EditableProperties(context.JsonTypeInfo.Type)
            .ToDictionary(property => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(property.Name), property =>
            {
                var (type, format) = _patchFieldSchemas[property.PropertyType];
                bool clears = clearable.Contains(System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(property.Name));
                return (IOpenApiSchema)new OpenApiSchema
                {
                    Type = type | (clears ? JsonSchemaType.Null : 0),
                    Format = format,
                    Items = type == JsonSchemaType.Array ? new OpenApiSchema { Type = JsonSchemaType.String } : null,
                    MaxItems = property.Name == "ManufacturerKeys" ? 16 : null,
                    UniqueItems = property.Name == "ManufacturerKeys" ? true : null
                };
            });
        return Task.CompletedTask;
    }

    private static Task ApplyReferenceRelationshipSchemaAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken ct)
    {
        if (context.JsonPropertyInfo?.AttributeProvider is PropertyInfo property &&
            (property.DeclaringType == typeof(SystemResource) || property.DeclaringType == typeof(ReferenceSystem) || property.DeclaringType == typeof(CreateSystem)) &&
            property.Name is "Manufacturers" or "ManufacturerKeys")
        {
            schema.MaxItems = 16;
            schema.UniqueItems = true;
        }
        return Task.CompletedTask;
    }

    private static Task ApplyOpaquePublicIdSchemaAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        Type serializedType = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ??
                              context.JsonTypeInfo.Type;
        // A bound system key is a string on the wire too: OpenAPI describes some bound parameters
        // by their CLR shape, which would publish the resolved id.
        if (serializedType != typeof(Sqid) && serializedType != typeof(SystemKeyParameter))
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
            schema.Pattern = _canonicalUnsignedIntegerPattern;
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
            ? _canonicalUnsignedIntegerPattern
            : _canonicalIntegerPattern;
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

    private static Task ApplyDeliveryFormatSchemaAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken ct)
    {
        if (context.JsonPropertyInfo?.AttributeProvider is not PropertyInfo property)
            return Task.CompletedTask;

        string[]? values = property.DeclaringType == typeof(RomDeliveryFormatMetadata)
            ? property.Name switch
            {
                nameof(RomDeliveryFormatMetadata.Format) => ["original", "rvz", "chd"],
                nameof(RomDeliveryFormatMetadata.State) => ["available", "preparing", "unsupported"],
                _ => null
            }
            : property.Name == nameof(ExportLibraryRequest.Format) &&
              (property.DeclaringType == typeof(ExportLibraryRequest) ||
               property.DeclaringType == typeof(CreateDownloadTicketRequest) ||
               property.DeclaringType == typeof(ArchiveFormat) ||
               property.DeclaringType == typeof(Romd.Contracts.Management.Models.ExportJob))
                ? ["original", "rvz", "chd"]
                : null;
        if (values is null)
            return Task.CompletedTask;

        schema.Type = JsonSchemaType.String;
        schema.Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
        return Task.CompletedTask;
    }

    private static Task ApplyDeliveryFormatOperationSchemaAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken ct)
    {
        if (operation.OperationId is not "DownloadRom")
            return Task.CompletedTask;

        foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>() ?? [])
        {
            if (parameter.In != ParameterLocation.Query || parameter.Name != "format")
                continue;
            parameter.Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Enum = [JsonValue.Create("original")!, JsonValue.Create("rvz")!, JsonValue.Create("chd")!]
            };
        }
        return Task.CompletedTask;
    }

    public static IServiceCollection AddRomdConsumerHttp(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        IHostEnvironment? environment = null,
        string openApiDocumentName = "v1")
    {
        services.AddRomdHttpContract();

        services.AddRomdCors(
            RomdCorsPolicyNames.Consumer,
            RomdHostSurfaceOptions.ConsumerSectionName,
            configuration,
            environment,
            _developmentConsumerCorsOrigins);
        services.AddOpenApi(openApiDocumentName, options =>
        {
            options.AddSchemaTransformer(ApplyOpaquePublicIdSchemaAsync);
            options.AddSchemaTransformer(ApplyReferencePatchSchemaAsync);
            options.AddSchemaTransformer(ApplyReferenceRelationshipSchemaAsync);
            options.AddSchemaTransformer(ApplyNumericWireSchemaAsync);
            options.AddSchemaTransformer(ApplyDeliveryFormatSchemaAsync);
            options.AddSchemaTransformer(ApplyConsumerCollectionSchemaAsync);
            options.AddSchemaTransformer(ApplyStrictEnumSchemaAsync);
            options.AddSchemaTransformer(ApplyProblemDetailsEnvelopeSchemaAsync);
            options.AddOperationTransformer(ApplyClosedValuesOperationSchemaAsync);
            AddRomdOpenApiSecurity(options);
        });

        return services;
    }

    private static Task ApplyClosedValuesOperationSchemaAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var handler = context.Description.ActionDescriptor.EndpointMetadata.OfType<MethodInfo>().LastOrDefault();
        if (handler is null)
            return Task.CompletedTask;
        var filters = handler.GetParameters()
            .Select(parameter => (Name: parameter.GetCustomAttribute<Microsoft.AspNetCore.Mvc.FromQueryAttribute>()?.Name ?? parameter.Name,
                Values: parameter.GetCustomAttribute<ClosedValuesAttribute>()?.Values))
            .Where(parameter => parameter.Name is not null && parameter.Values is not null)
            .ToDictionary(parameter => parameter.Name!, parameter => parameter.Values!, StringComparer.Ordinal);
        foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>() ?? [])
        {
            if (parameter.In != ParameterLocation.Query || !filters.TryGetValue(parameter.Name!, out var values))
                continue;
            // Mutate the generated schema so its default, length and nullability metadata survive.
            var schema = parameter.Schema as OpenApiSchema ?? new OpenApiSchema();
            schema.Type = JsonSchemaType.String | ((schema.Type ?? 0) & JsonSchemaType.Null);
            schema.Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
            parameter.Schema = schema;
        }
        return Task.CompletedTask;
    }

    // Bounded identity collections publish the invariants the handler enforces
    // (docs/decisions/api-contract-policy.md, "Opaque Public Identity").
    private static Task ApplyConsumerCollectionSchemaAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.JsonPropertyInfo?.AttributeProvider is not PropertyInfo property ||
            property.DeclaringType != typeof(ConsumerReleasePreference) ||
            property.Name is not (nameof(ConsumerReleasePreference.PreferredRegionKeys)
                or nameof(ConsumerReleasePreference.PreferredLanguageKeys)))
        {
            return Task.CompletedTask;
        }

        schema.MaxItems = ConsumerReleasePreference.MaximumPreferredKeys;
        schema.UniqueItems = true;
        return Task.CompletedTask;
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
            // Refuse incompatible application databases before Hangfire can install or upgrade.
            services.AddHostedService<PostgreSqlSchemaProvisioner>();
            services.AddHostedService<HangfireSchemaProvisioner>();
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

        // QueueSafety_ReplaceJobsSharingSource proves overlapping replacement ingestion
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
            // QueueSafety_SingleAndBulkEnrichmentShareTitle reproduces a provider-layer
            // unique-key collision. Distinct job fences do not serialize title/evidence writes.
            options.WorkerCount = 1;
        });

        services.AddHangfireServer(options =>
        {
            options.ServerName = "materialization-worker";
            options.Queues = ["materialization"];
            // QueueSafety_Materialization tests cover shared catalogs, per-library
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

    internal static long GetMaxUploadBytes(IRomdOptions romdOptions) =>
        romdOptions.MaxUploadBytes > 0
            ? romdOptions.MaxUploadBytes
            : 64L * 1024 * 1024 * 1024;

    private sealed class RomdCorsDefaultEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Romd";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
