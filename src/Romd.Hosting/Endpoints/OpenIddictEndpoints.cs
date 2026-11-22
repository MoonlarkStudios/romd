using Microsoft.AspNetCore.WebUtilities;
using Romd.Host.Authorization;
using Romd.Application.Common.Security;
using Romd.Application.Common.Security.Commands.StartAccountSession;
using Romd.Application.Common.Cqrs;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.HttpResults;
using Romd.Hosting.Security;
using Microsoft.AspNetCore.Http.Features;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Romd.Hosting;
using Romd.Infrastructure.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Romd.Host.Endpoints;

public static class OpenIddictEndpoints
{
    private const string _appLoginClientClaim = "romd_login_client";
    private const string _confirmedCodeField = "confirmed_code";

    public static IEndpointRouteBuilder MapRomdOpenIddictEndpoints(this IEndpointRouteBuilder app)
    {
        // The OAuth/OIDC protocol endpoints are driven by oidc-client-ts (SPAs) and the device flow
        // (console/mount) directly, never through the generated typed SDK, so they are excluded from the
        // OpenAPI description to keep the client free of dead operations.
        app.MapGet("/connect/authorize", Authorize)
            .WithName("OpenIddictAuthorize")
            .ExcludeFromDescription()
            .AllowAnonymous();

        app.MapGet("/connect/login", ShowLogin)
            .WithName("OpenIddictLogin")
            .ExcludeFromDescription()
            .AllowAnonymous();

        app.MapPost("/connect/login", SubmitLogin)
            .WithName("OpenIddictSubmitLogin")
            .WithMetadata(new RequestSizeLimitAttribute(InteractiveAuthenticationForms.MaxBodyBytes))
            .ExcludeFromDescription()
            .AllowAnonymous();

        app.MapMethods("/connect/logout", ["GET", "POST"], Logout)
            .WithName("OpenIddictLogout")
            .ExcludeFromDescription()
            .DisableAntiforgery()
            .AllowAnonymous();

        app.MapGet("/connect/verify", VerifyDeviceAuthorization)
            .WithName("VerifyDeviceAuthorization")
            .ExcludeFromDescription()
            .AllowAnonymous();

        app.MapPost("/connect/verify", ApproveDeviceAuthorization)
            .WithName("ApproveDeviceAuthorization")
            .WithMetadata(new RequestSizeLimitAttribute(InteractiveAuthenticationForms.MaxBodyBytes))
            .ExcludeFromDescription()
            .AllowAnonymous();

        app.MapPost("/connect/token", ExchangeToken)
            .WithName("ExchangeOpenIddictToken")
            .ExcludeFromDescription()
            .DisableAntiforgery()
            .AllowAnonymous();

        return app;
    }

    private static async Task<IResult> Authorize(
        HttpContext context,
        RomdOpenIddictAccountService accountService,
        RomdSurfaceAuthOptions surface,
        IAccountSessions sessions,
        IConsumerWebAppRegistrations registrations,
        ICommandHandler<StartAccountSessionCommand, AccountSession> startSession,
        CancellationToken cancellationToken)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        string clientId = request.ClientId!;
        var app = await surface.FindActiveWebAppAsync(clientId, registrations, cancellationToken);
        if (!surface.IsClientAllowed(clientId) && app is null)
            return ForbidWithError(Errors.UnauthorizedClient, "This client is not allowed on this host.");

        string loginScheme = app is null
            ? RomdHostRegistrationExtensions.InteractiveLoginScheme
            : RomdHostRegistrationExtensions.RegisteredWebAppLoginScheme;
        string returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
        IResult RedirectToLogin() => Results.Redirect($"/connect/login?returnUrl={Uri.EscapeDataString(returnUrl)}");

        var loginResult = await context.AuthenticateAsync(loginScheme);
        string? userId = loginResult.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!loginResult.Succeeded || string.IsNullOrEmpty(userId)
            || app is not null && loginResult.Principal?.FindFirst(_appLoginClientClaim)?.Value != clientId)
            return RedirectToLogin();

        var principal = await accountService.CreatePrincipalAsync(
            userId,
            request.GetScopes(),
            surface.Audience,
            clientId!,
            cancellationToken);
        if (principal is null)
        {
            // The user exists but is not eligible for this client (e.g. a consumer on the admin SPA).
            await context.SignOutAsync(loginScheme);
            return ForbidWithError(Errors.AccessDenied, "This account is not allowed to use this application.");
        }

        // An interactive cookie is bound to the same server session as its browser tokens.
        // Revoking that session must not let the cookie silently mint another session.
        var stamp = principal.GetClaim(SessionClaims.AccountStamp)!;
        if (loginResult.Principal!.FindFirst(SessionClaims.AccountStamp)?.Value != stamp)
        {
            await context.SignOutAsync(loginScheme);
            return RedirectToLogin();
        }
        var cookieSession = loginResult.Principal.FindFirst(SessionClaims.Id)?.Value;
        AccountSession? session;
        if (Guid.TryParse(cookieSession, out var existingId))
        {
            session = await sessions.ValidateAsync(Guid.Parse(userId), existingId, stamp, cancellationToken);
            if (session is null || session.ClientId != clientId)
            {
                await context.SignOutAsync(loginScheme);
                return RedirectToLogin();
            }
        }
        else
        {
            string browser = DescribeBrowser(context.Request.Headers.UserAgent.ToString());
            var started = await startSession.HandleAsync(new(Guid.Parse(userId), clientId, app is null ? browser : $"{app.DisplayName} · {browser}", stamp), cancellationToken);
            // Session creation has no business rejection path; faults retain the existing authentication error boundary.
            if (started.IsError)
                throw new InvalidOperationException("Account session creation unexpectedly returned an error.");
            session = started.Value;
            var identity = (ClaimsIdentity)loginResult.Principal.Identity!;
            identity.AddClaim(new Claim(SessionClaims.Id, session.Id.ToString()));
            await context.SignInAsync(loginScheme, loginResult.Principal);
        }
        AttachSession(principal, session);
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> ShowLogin(
        HttpContext context,
        IAntiforgery antiforgery,
        RomdSurfaceAuthOptions surface,
        ISignInMessageReader signInMessage,
        IConsumerWebAppRegistrations registrations,
        CancellationToken ct)
    {
        string returnUrl = SanitizeReturnUrl(context.Request.Query["returnUrl"].FirstOrDefault());
        string message = await signInMessage.GetMessageAsync(ct);
        var app = await surface.FindActiveWebAppAsync(ReadReturnClient(returnUrl), registrations, ct);

        return Results.Content(RenderLoginPage(returnUrl, antiforgery.GetAndStoreTokens(context), message,
            error: null, isAdmin: surface.IsClientAllowed(RomdOpenIddictClients.AdminSpa), app), "text/html");
    }

    private static async Task<IResult> SubmitLogin(
        HttpContext context,
        RomdOpenIddictAccountService accountService,
        IAntiforgery antiforgery,
        AuthenticationAttemptLimiter attempts,
        RomdSurfaceAuthOptions surface,
        ISignInMessageReader signInMessage,
        IConsumerWebAppRegistrations registrations,
        CancellationToken cancellationToken)
    {
        if (!await IsValidFormAsync(context, antiforgery))
            return Results.BadRequest();
        if (attempts.TryAcquireIp(context.Connection.RemoteIpAddress) is { } ipDelay)
            return Throttled(context, ipDelay);
        var form = await context.Request.ReadFormAsync(cancellationToken);
        string returnUrl = SanitizeReturnUrl(form["returnUrl"].FirstOrDefault());
        string? returnClient = ReadReturnClient(returnUrl);
        var app = await surface.FindActiveWebAppAsync(returnClient, registrations, cancellationToken);
        if (surface.AllowsRegisteredWebApps && ConsumerWebAppRegistration.IsClientId(returnClient) && app is null)
            return Results.BadRequest();
        string login = form["login"].FirstOrDefault() ?? string.Empty;
        string password = form["password"].FirstOrDefault() ?? string.Empty;

        if (login.Length > 320 || password.Length > RomdPasswordLengthValidator.MaximumLength)
            return Results.BadRequest();
        using var verification = attempts.TryEnterVerification();
        if (verification is null)
            return Throttled(context, TimeSpan.FromSeconds(1));
        var candidate = await accountService.FindByLoginAsync(login, cancellationToken);
        if (attempts.TryAcquireAccount(candidate?.Id, login) is { } accountDelay)
            return Throttled(context, accountDelay);
        var user = await accountService.ValidateCredentialsAsync(candidate, password, cancellationToken);
        if (user is null)
        {
            string message = await signInMessage.GetMessageAsync(cancellationToken);
            return Results.Content(
                RenderLoginPage(returnUrl, antiforgery.GetAndStoreTokens(context), message,
                    "Invalid ROMD credentials.", isAdmin: surface.IsClientAllowed(RomdOpenIddictClients.AdminSpa), app),
                "text/html",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        string scheme = app is null
            ? RomdHostRegistrationExtensions.InteractiveLoginScheme
            : RomdHostRegistrationExtensions.RegisteredWebAppLoginScheme;
        var identity = new ClaimsIdentity(scheme);
        if (app is not null)
            identity.AddClaim(new Claim(_appLoginClientClaim, app.ClientId));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(SessionClaims.AccountStamp, user.SecurityStamp!));
        await context.SignInAsync(
            scheme,
            new ClaimsPrincipal(identity));

        return Results.LocalRedirect(returnUrl);
    }

    private static async Task<IResult> Logout(HttpContext context, IAccountSessions sessions, CancellationToken cancellationToken)
    {
        var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (Guid.TryParse(result.Principal?.GetClaim(Claims.Subject), out var userId)
            && Guid.TryParse(result.Principal?.GetClaim(SessionClaims.Id), out var sessionId))
            await sessions.RevokeAsync(userId, sessionId, null, userId, "Signed out", cancellationToken);
        await context.SignOutAsync(RomdHostRegistrationExtensions.InteractiveLoginScheme);

        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }

    private static async Task<IResult> VerifyDeviceAuthorization(HttpContext context, IAntiforgery antiforgery)
    {
        var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        string? userCode = result.Properties?.GetTokenValue(OpenIddictServerAspNetCoreConstants.Tokens.UserCode)
            ?? context.Request.Query[Parameters.UserCode].FirstOrDefault();

        var pairing = result is { Succeeded: true } ? result.Principal : null;
        string? error = pairing is null && !string.IsNullOrEmpty(userCode) ? "The device code is invalid or expired." : null;
        return VerificationPage(context, antiforgery, userCode, pairing, error);
    }

    private static async Task<IResult> ApproveDeviceAuthorization(
        HttpContext context,
        RomdOpenIddictAccountService accountService,
        RomdSurfaceAuthOptions surface,
        ICommandHandler<StartAccountSessionCommand, AccountSession> startSession,
        IAntiforgery antiforgery,
        AuthenticationAttemptLimiter attempts,
        CancellationToken cancellationToken)
    {
        if (!await IsValidFormAsync(context, antiforgery))
            return Results.BadRequest();
        if (attempts.TryAcquireIp(context.Connection.RemoteIpAddress) is { } ipDelay)
            return Throttled(context, ipDelay);
        context.Request.EnableBuffering();
        var form = await context.Request.ReadFormAsync(cancellationToken);
        context.Request.Body.Position = 0;

        var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        string? clientId = result.Principal?.GetClaim(Claims.ClientId);
        string? userCode = form[Parameters.UserCode].FirstOrDefault();
        if (!result.Succeeded || !surface.IsClientAllowed(clientId))
            return VerificationPage(context, antiforgery, userCode, null, "The device code is invalid or expired.",
                StatusCodes.Status400BadRequest);

        bool community = clientId == RomdOpenIddictClients.CommunityDevice;
        if (community && form[_confirmedCodeField].FirstOrDefault() != userCode)
            return VerificationPage(context, antiforgery, userCode, result.Principal);

        string login = form["login"].FirstOrDefault() ?? string.Empty;
        string password = form["password"].FirstOrDefault() ?? string.Empty;
        if (login.Length > 320 || password.Length > RomdPasswordLengthValidator.MaximumLength)
            return Results.BadRequest();
        using var verification = attempts.TryEnterVerification();
        if (verification is null)
            return Throttled(context, TimeSpan.FromSeconds(1));
        var candidate = await accountService.FindByLoginAsync(login, cancellationToken);
        if (attempts.TryAcquireAccount(candidate?.Id, login) is { } accountDelay)
            return Throttled(context, accountDelay);
        var user = await accountService.ValidateCredentialsAsync(candidate, password, cancellationToken);
        var principal = user is null
            ? null
            : await accountService.CreatePrincipalAsync(
                user,
                result.Principal!.GetScopes(),
                surface.Audience,
                clientId!,
                cancellationToken);
        if (principal is null)
            return VerificationPage(context, antiforgery, userCode, result.Principal, "Invalid ROMD credentials.",
                StatusCodes.Status401Unauthorized);

        string deviceLabel = clientId switch
        {
            RomdOpenIddictClients.CommunityDevice => result.Principal!.GetClaim(CommunityDeviceAuthorization.LabelClaim) ?? "Community device",
            RomdOpenIddictClients.Mount => "ROMD Mount",
            _ => "Console"
        };
        var started = await startSession.HandleAsync(new(user!.Id, clientId!, deviceLabel, user.SecurityStamp!), cancellationToken);
        if (started.IsError)
            throw new InvalidOperationException("Account session creation unexpectedly returned an error.");
        var session = started.Value;
        AttachSession(principal, session);
        return Results.SignIn(
            principal,
            new AuthenticationProperties { RedirectUri = "/" },
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> ExchangeToken(
        HttpContext context,
        RomdOpenIddictAccountService accountService,
        RomdSurfaceAuthOptions surface,
        IAccountSessions sessions,
        IConsumerWebAppRegistrations registrations,
        CancellationToken cancellationToken)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");
        if (!request.IsAuthorizationCodeGrantType()
            && !request.IsDeviceCodeGrantType()
            && !request.IsRefreshTokenGrantType())
        {
            throw new InvalidOperationException("The specified grant type is not supported.");
        }

        var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        string? userId = result.Principal?.GetClaim(Claims.Subject);
        string? clientId = request.ClientId;
        if (userId is null || !surface.IsClientAllowed(clientId) &&
            await surface.FindActiveWebAppAsync(clientId, registrations, cancellationToken) is null)
        {
            return ForbidWithError(Errors.InvalidGrant, "The token is no longer valid.");
        }

        var principal = await accountService.CreatePrincipalAsync(
            userId,
            request.IsRefreshTokenGrantType() && request.GetScopes() is { Length: > 0 } requested
                ? result.Principal!.GetScopes().Intersect(requested, StringComparer.Ordinal)
                : result.Principal!.GetScopes(),
            surface.Audience,
            clientId!,
            cancellationToken);

        if (principal is null || principal.GetClaim(SessionClaims.AccountStamp) != result.Principal!.GetClaim(SessionClaims.AccountStamp)
            || !Guid.TryParse(result.Principal!.GetClaim(SessionClaims.Id), out var sessionId))
            return ForbidWithError(Errors.InvalidGrant, "Sign in again to continue.");
        var session = await sessions.ValidateAsync(Guid.Parse(userId), sessionId, principal.GetClaim(SessionClaims.AccountStamp)!, cancellationToken);
        if (session is null || session.ClientId != clientId)
            return ForbidWithError(Errors.InvalidGrant, "This session has ended. Sign in again.");
        if (clientId != RomdOpenIddictClients.CommunityDevice && request.IsDeviceCodeGrantType() && request.GetParameter("device_name")?.ToString() is { Length: > 0 } device)
            await sessions.SetDeviceAsync(Guid.Parse(userId), sessionId, device, cancellationToken);
        AttachSession(principal, session);
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static string? ReadReturnClient(string returnUrl)
    {
        int queryStart = returnUrl.IndexOf('?');
        return queryStart > 0 && returnUrl[..queryStart] == "/connect/authorize"
            ? QueryHelpers.ParseQuery(returnUrl[queryStart..])["client_id"].FirstOrDefault()
            : null;
    }

    private static void AttachSession(ClaimsPrincipal principal, AccountSession session)
    {
        principal.SetClaim(SessionClaims.Id, session.Id.ToString());
        principal.FindFirst(SessionClaims.Id)!.SetDestinations(Destinations.AccessToken, Destinations.IdentityToken);
        var remaining = session.ExpiresAt - DateTimeOffset.UtcNow;
        principal.SetAccessTokenLifetime(remaining < TimeSpan.FromMinutes(15) ? remaining : TimeSpan.FromMinutes(15));
        principal.SetRefreshTokenLifetime(remaining);
    }

    private static string DescribeBrowser(string agent)
    {
        string browser = agent.Contains("Edg/") ? "Edge" : agent.Contains("Firefox/") ? "Firefox" :
            agent.Contains("Chrome/") ? "Chrome" : agent.Contains("Safari/") ? "Safari" : "Browser";
        string os = agent.Contains("Android") ? "Android" : agent.Contains("iPhone") || agent.Contains("iPad") ? "iOS" :
            agent.Contains("Windows") ? "Windows" : agent.Contains("Macintosh") ? "macOS" : agent.Contains("Linux") ? "Linux" : "Unknown device";
        return $"{browser} on {os}";
    }

    private static IResult ForbidWithError(string error, string description) =>
        Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
            }),
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    // Only allow local return paths so a crafted returnUrl cannot turn login into an open redirect.
    private static string SanitizeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && !returnUrl.Any(char.IsControl) && RedirectHttpResult.IsLocalUrl(returnUrl)
            ? returnUrl
            : "/";

    private static string RenderLoginPage(string returnUrl, AntiforgeryTokenSet tokens, string message, string? error, bool isAdmin, ConsumerWebAppRegistration? app)
    {
        string encodedReturnUrl = WebUtility.HtmlEncode(returnUrl);
        string appMarkup = app is null ? "" :
            $"""<p class="romd-auth-description">Sign in to {WebUtility.HtmlEncode(app.DisplayName)} ({WebUtility.HtmlEncode(app.BrowserOrigin)}).</p>""";
        string messageMarkup = string.IsNullOrWhiteSpace(message)
            ? string.Empty
            : $"""<p class="romd-auth-description romd-auth-message">{WebUtility.HtmlEncode(message)}</p>""";
        string errorMarkup = string.IsNullOrWhiteSpace(error)
            ? string.Empty
            : $"""<p class="romd-auth-error" role="alert">{WebUtility.HtmlEncode(error)}</p>""";

        return $$"""
            <!doctype html>
            <html lang="en" data-romd-portal="{{(isAdmin ? "admin" : "consumer")}}">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>ROMD Sign In</title>
              {{AuthenticationPageAssets.Head}}
            </head>
            <body class="romd-auth-shell">
              <main class="romd-auth-card">
                {{AuthenticationPageAssets.Brand}}
                {{appMarkup}}
                {{messageMarkup}}
                {{errorMarkup}}
                <form method="post" action="/connect/login" autocomplete="on">
                  {{RenderAntiforgeryField(tokens)}}
                  <input type="hidden" name="returnUrl" value="{{encodedReturnUrl}}">

                  <label for="login">Email or username</label>
                  <input id="login" name="login" autocomplete="username" autocapitalize="none" spellcheck="false" required>

                  <label for="password">Password</label>
                  <input id="password" name="password" type="password" autocomplete="current-password" required>

                  <button class="romd-auth-submit" type="submit">Sign in</button>
                </form>
              </main>
            </body>
            </html>
            """;
    }

    private static IResult VerificationPage(HttpContext context, IAntiforgery antiforgery, string? userCode,
        ClaimsPrincipal? pairing, string? error = null, int status = StatusCodes.Status200OK)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        string antiforgeryField = RenderAntiforgeryField(antiforgery.GetAndStoreTokens(context));
        string code = WebUtility.HtmlEncode(userCode ?? "");
        string errorMarkup = error is null ? "" : $"""<p class="romd-auth-error" role="alert">{WebUtility.HtmlEncode(error)}</p>""";
        string codeField = $"""
            <label for="user_code">Device code</label>
            <input id="user_code" name="user_code" value="{code}" autocomplete="one-time-code" required>
            """;

        string form;
        if (pairing is null)
        {
            form = $$"""
                <form method="get" action="/connect/verify">
                  {{codeField}}
                  <button class="romd-auth-submit" type="submit">Continue</button>
                </form>
                {{antiforgeryField}}
                """;
        }
        else
        {
            string confirmation = pairing.GetClaim(Claims.ClientId) == RomdOpenIddictClients.CommunityDevice
                ? RenderCommunityPairing(pairing, code)
                : codeField;
            form = $$"""
                <form method="post" action="/connect/verify" autocomplete="on">
                  {{antiforgeryField}}
                  {{confirmation}}

                  <label for="login">Email or username</label>
                  <input id="login" name="login" autocomplete="username" autocapitalize="none" spellcheck="false" required>

                  <label for="password">Password</label>
                  <input id="password" name="password" type="password" autocomplete="current-password" required>

                  <button class="romd-auth-submit" type="submit">Link device</button>
                </form>
                """;
        }

        return Results.Content($$"""
            <!doctype html>
            <html lang="en" data-romd-portal="consumer">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>ROMD Device Sign In</title>
              {{AuthenticationPageAssets.Head}}
            </head>
            <body class="romd-auth-shell">
              <main class="romd-auth-card">
                {{AuthenticationPageAssets.Brand}}
                <p class="romd-auth-description">Enter the code shown by your ROMD client and sign in to link this device.</p>
                {{errorMarkup}}
                {{form}}
              </main>
            </body>
            </html>
            """, "text/html", statusCode: status);
    }

    private static string RenderCommunityPairing(ClaimsPrincipal pairing, string code)
    {
        string label = WebUtility.HtmlEncode(pairing.GetClaim(CommunityDeviceAuthorization.LabelClaim) ?? "Community device");
        string[] permissions = ConsumerGrantScopes.All.Where(pairing.HasScope).Select(scope => scope switch
        {
            ConsumerGrantScopes.Discovery => "Identify your ROMD account",
            ConsumerGrantScopes.Library => "Browse your assigned library",
            ConsumerGrantScopes.Releases => "View available releases",
            ConsumerGrantScopes.Delivery => "Download content you can access",
            _ => throw new InvalidOperationException("Unknown consumer permission.")
        }).ToArray();
        string permissionList = permissions.Length == 0 ? "" : $"<ul><li>{string.Join("</li><li>", permissions)}</li></ul>";
        return $$"""
            <p class="romd-auth-description">Confirm that the device in front of you shows <strong>{{code}}</strong>.</p>
            <p class="romd-auth-description">The app supplied the device label <strong>{{label}}</strong>.</p>
            {{permissionList}}
            <input type="hidden" name="user_code" value="{{code}}">
            <input type="hidden" name="{{_confirmedCodeField}}" value="{{code}}">
            """;
    }

    private static string RenderAntiforgeryField(AntiforgeryTokenSet tokens) =>
        $"""<input type="hidden" name="{WebUtility.HtmlEncode(tokens.FormFieldName)}" value="{WebUtility.HtmlEncode(tokens.RequestToken)}">""";

    private static async Task<bool> IsValidFormAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static IResult Throttled(HttpContext context, TimeSpan delay)
    {
        context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds))
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Text("Too many sign-in attempts. Try again later.", statusCode: StatusCodes.Status429TooManyRequests);
    }

}
