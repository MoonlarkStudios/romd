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
        ICommandHandler<StartAccountSessionCommand, AccountSession> startSession,
        CancellationToken cancellationToken)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        string? clientId = request.ClientId;
        if (!surface.IsClientAllowed(clientId))
        {
            return ForbidWithError(Errors.UnauthorizedClient, "This client is not allowed on this host.");
        }

        var loginResult = await context.AuthenticateAsync(RomdHostRegistrationExtensions.InteractiveLoginScheme);
        string? userId = loginResult.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (loginResult is not { Succeeded: true } || string.IsNullOrEmpty(userId))
        {
            string returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
            return Results.Redirect($"/connect/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        var principal = await accountService.CreatePrincipalAsync(
            userId,
            request.GetScopes(),
            surface.Audience,
            clientId!,
            cancellationToken);
        if (principal is null)
        {
            // The user exists but is not eligible for this client (e.g. a consumer on the admin SPA).
            await context.SignOutAsync(RomdHostRegistrationExtensions.InteractiveLoginScheme);
            return ForbidWithError(Errors.AccessDenied, "This account is not allowed to use this application.");
        }

        // An interactive cookie is bound to the same server session as its browser tokens.
        // Revoking that session must not let the cookie silently mint another session.
        var stamp = principal.GetClaim(SessionClaims.AccountStamp)!;
        if (loginResult.Principal!.FindFirst(SessionClaims.AccountStamp)?.Value != stamp)
        {
            await context.SignOutAsync(RomdHostRegistrationExtensions.InteractiveLoginScheme);
            return Results.Redirect($"/connect/login?returnUrl={Uri.EscapeDataString(context.Request.Path + context.Request.QueryString)}");
        }
        var cookieSession = loginResult.Principal.FindFirst(SessionClaims.Id)?.Value;
        AccountSession? session;
        if (Guid.TryParse(cookieSession, out var existingId))
        {
            session = await sessions.ValidateAsync(Guid.Parse(userId), existingId, stamp, cancellationToken);
            if (session is null || session.ClientId != clientId)
            {
                await context.SignOutAsync(RomdHostRegistrationExtensions.InteractiveLoginScheme);
                return Results.Redirect($"/connect/login?returnUrl={Uri.EscapeDataString(context.Request.Path + context.Request.QueryString)}");
            }
        }
        else
        {
            var started = await startSession.HandleAsync(new(Guid.Parse(userId), clientId!, DescribeBrowser(context.Request.Headers.UserAgent.ToString()), stamp), cancellationToken);
            // Session creation has no business rejection path; faults retain the existing authentication error boundary.
            if (started.IsError)
                throw new InvalidOperationException("Account session creation unexpectedly returned an error.");
            session = started.Value;
            var identity = (ClaimsIdentity)loginResult.Principal.Identity!;
            identity.AddClaim(new Claim(SessionClaims.Id, session.Id.ToString()));
            await context.SignInAsync(RomdHostRegistrationExtensions.InteractiveLoginScheme, loginResult.Principal);
        }
        AttachSession(principal, session);
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static IResult ShowLogin(HttpContext context, IAntiforgery antiforgery, RomdSurfaceAuthOptions surface)
    {
        string returnUrl = SanitizeReturnUrl(context.Request.Query["returnUrl"].FirstOrDefault());

        return Results.Content(RenderLoginPage(returnUrl, antiforgery.GetAndStoreTokens(context), error: null, isAdmin: surface.IsClientAllowed("romd-admin-spa")), "text/html");
    }

    private static async Task<IResult> SubmitLogin(
        HttpContext context,
        RomdOpenIddictAccountService accountService,
        IAntiforgery antiforgery,
        AuthenticationAttemptLimiter attempts,
        RomdSurfaceAuthOptions surface,
        CancellationToken cancellationToken)
    {
        if (!await IsValidFormAsync(context, antiforgery))
            return Results.BadRequest();
        if (attempts.TryAcquireIp(context.Connection.RemoteIpAddress) is { } ipDelay)
            return Throttled(context, ipDelay);
        var form = await context.Request.ReadFormAsync(cancellationToken);
        string returnUrl = SanitizeReturnUrl(form["returnUrl"].FirstOrDefault());
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
            return Results.Content(
                RenderLoginPage(returnUrl, antiforgery.GetAndStoreTokens(context), "Invalid ROMD credentials.", isAdmin: surface.IsClientAllowed("romd-admin-spa")),
                "text/html",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var identity = new ClaimsIdentity(RomdHostRegistrationExtensions.InteractiveLoginScheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(SessionClaims.AccountStamp, user.SecurityStamp!));
        await context.SignInAsync(
            RomdHostRegistrationExtensions.InteractiveLoginScheme,
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

        bool hasValidCode = result is { Succeeded: true }
            && !string.IsNullOrEmpty(result.Principal?.GetClaim(Claims.ClientId));
        string? error = !hasValidCode && !string.IsNullOrEmpty(userCode)
            ? "The device code is invalid or expired."
            : null;

        return Results.Content(
            RenderVerificationPage(userCode ?? string.Empty, antiforgery.GetAndStoreTokens(context), error),
            "text/html");
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
        if (result is not { Succeeded: true } || string.IsNullOrEmpty(clientId))
        {
            string userCode = form[Parameters.UserCode].FirstOrDefault() ?? string.Empty;

            return Results.Content(
                RenderVerificationPage(userCode, antiforgery.GetAndStoreTokens(context), "The device code is invalid or expired."),
                "text/html",
                statusCode: StatusCodes.Status400BadRequest);
        }

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
                clientId,
                cancellationToken);
        if (principal is null)
        {
            string userCode = result.Properties?.GetTokenValue(OpenIddictServerAspNetCoreConstants.Tokens.UserCode)
                ?? form[Parameters.UserCode].FirstOrDefault()
                ?? string.Empty;

            return Results.Content(
                RenderVerificationPage(userCode, antiforgery.GetAndStoreTokens(context), "Invalid ROMD credentials."),
                "text/html",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        string deviceLabel = clientId == RomdOpenIddictClients.Mount ? "ROMD Mount" : "Console";
        var started = await startSession.HandleAsync(new(user!.Id, clientId, deviceLabel, user.SecurityStamp!), cancellationToken);
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
        if (userId is null || !surface.IsClientAllowed(clientId))
        {
            return ForbidWithError(Errors.InvalidGrant, "The token is no longer valid.");
        }

        var principal = await accountService.CreatePrincipalAsync(
            userId,
            result.Principal!.GetScopes(),
            surface.Audience,
            clientId!,
            cancellationToken);

        if (principal is null || principal.GetClaim(SessionClaims.AccountStamp) != result.Principal!.GetClaim(SessionClaims.AccountStamp)
            || !Guid.TryParse(result.Principal!.GetClaim(SessionClaims.Id), out var sessionId))
            return ForbidWithError(Errors.InvalidGrant, "Sign in again to continue.");
        var session = await sessions.ValidateAsync(Guid.Parse(userId), sessionId, principal.GetClaim(SessionClaims.AccountStamp)!, cancellationToken);
        if (session is null || session.ClientId != clientId)
            return ForbidWithError(Errors.InvalidGrant, "This session has ended. Sign in again.");
        if (request.IsDeviceCodeGrantType() && request.GetParameter("device_name")?.ToString() is { Length: > 0 } device)
            await sessions.SetDeviceAsync(Guid.Parse(userId), sessionId, device, cancellationToken);
        AttachSession(principal, session);
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
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

    private static string RenderLoginPage(string returnUrl, AntiforgeryTokenSet tokens, string? error, bool isAdmin)
    {
        string encodedReturnUrl = WebUtility.HtmlEncode(returnUrl);
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
                <p class="romd-auth-description">{{(isAdmin ? "Manage your library" : "Sign in to your library")}}</p>
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

    private static string RenderVerificationPage(string userCode, AntiforgeryTokenSet tokens, string? error)
    {
        string encodedUserCode = WebUtility.HtmlEncode(userCode);
        string errorMarkup = string.IsNullOrWhiteSpace(error)
            ? string.Empty
            : $"""<p class="romd-auth-error" role="alert">{WebUtility.HtmlEncode(error)}</p>""";

        return $$"""
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
                <form method="post" action="/connect/verify" autocomplete="on">
                  {{RenderAntiforgeryField(tokens)}}
                  <label for="user_code">Device code</label>
                  <input id="user_code" name="user_code" value="{{encodedUserCode}}" autocomplete="one-time-code" required>

                  <label for="login">Email or username</label>
                  <input id="login" name="login" autocomplete="username" autocapitalize="none" spellcheck="false" required>

                  <label for="password">Password</label>
                  <input id="password" name="password" type="password" autocomplete="current-password" required>

                  <button class="romd-auth-submit" type="submit">Link device</button>
                </form>
              </main>
            </body>
            </html>
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
