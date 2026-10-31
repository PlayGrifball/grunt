using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System.Net;
using System.Security.Claims;

namespace Surprenant.Grunt.Gateway.Host.Auth;

/// <summary>
/// Browser sessions for the admin page: OpenID Connect (Authorization Code + PKCE) against <see cref="OidcOptions.Authority"/>, then an
/// HttpOnly cookie scoped to <c>/admin</c>. Only the identity provider's own tokens are trusted: no identity header a proxy (or a pod
/// calling the Service directly) could set is ever read.
/// </summary>
public static class AdminSession
{
    public const string CookieScheme = "AdminSession";
    public const string OidcScheme = "AdminOidc";
    public const string CookieName = "grunt-admin";
    public const string CookiePath = "/admin";
    public const string CallbackPath = "/admin/signin-oidc";
    public const string SignedOutCallbackPath = "/admin/signout-callback-oidc";
    public const string RemoteSignOutPath = "/admin/signout-oidc";

    /// <summary>Set on principals that came from a browser session (cookie), which need an antiforgery token for unsafe methods.</summary>
    public const string SessionClaim = "grunt:session";

    private static readonly string[] DefaultScopes = { "openid", "profile", "email" };

    public static AuthenticationBuilder AddAdminSessions(this AuthenticationBuilder auth, OidcOptions oidc)
    {
        auth.Services.AddAntiforgery(o =>
        {
            o.Cookie.Name = "grunt-admin-af";
            o.Cookie.Path = CookiePath;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.SuppressXFrameOptionsHeader = true; // AdminPage sets its own (DENY)
        });

        if (!oidc.Enabled)
            return auth;

        auth.AddCookie(CookieScheme, o =>
        {
            o.Cookie.Name = CookieName;
            o.Cookie.Path = CookiePath;
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.ExpireTimeSpan = oidc.SessionLifetime;
            o.SlidingExpiration = false;
            // Never redirect API calls: no session is 401, a session without the admin group 403.
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        });

        auth.AddOpenIdConnect(OidcScheme, o =>
        {
            o.SignInScheme = CookieScheme;
            o.Authority = oidc.Authority;
            o.ClientId = oidc.ClientId;
            o.ClientSecret = string.IsNullOrEmpty(oidc.ClientSecret) ? null : oidc.ClientSecret;
            o.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
            o.ResponseType = OpenIdConnectResponseType.Code;
            o.UsePkce = true;
            o.CallbackPath = CallbackPath;
            o.SignedOutCallbackPath = SignedOutCallbackPath;
            o.RemoteSignOutPath = RemoteSignOutPath;
            o.SaveTokens = false;
            o.MapInboundClaims = false;
            o.GetClaimsFromUserInfoEndpoint = true;
            o.Scope.Clear();
            foreach (var scope in oidc.Scopes.Count > 0 ? (IEnumerable<string>)oidc.Scopes : DefaultScopes)
                o.Scope.Add(scope);
            o.ClaimActions.MapJsonKey(oidc.GroupClaim, oidc.GroupClaim);
            o.ClaimActions.MapUniqueJsonKey("preferred_username", "preferred_username");
            o.TokenValidationParameters.NameClaimType = "preferred_username";
            o.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
            o.Events.OnTicketReceived = context =>
            {
                if (!TryGrantAdmin(context.Principal, oidc))
                    context.Fail(new AdminGroupRequiredException());
                return Task.CompletedTask;
            };
            o.Events.OnRemoteFailure = async context =>
            {
                context.HandleResponse();
                var denied = context.Failure is AdminGroupRequiredException;
                context.Response.StatusCode = denied ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest;
                AdminPageHtml.ApplySecurityHeaders(context.Response);
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(AdminPageHtml.Message(
                    denied ? "Not allowed" : "Sign-in failed",
                    denied
                        ? "Your account is not in a group allowed to manage the Halo gateway."
                        : "The sign-in could not be completed (it may have expired). Try again.",
                    ("Back to the admin page", CookiePath)));
            };
        });

        return auth;
    }

    /// <summary>
    /// True when <paramref name="principal"/> carries one of <see cref="OidcOptions.AllowedGroups"/> in <see cref="OidcOptions.GroupClaim"/>;
    /// then it gets the admin role and the session marker.
    /// </summary>
    public static bool TryGrantAdmin(ClaimsPrincipal? principal, OidcOptions oidc)
    {
        if (principal?.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            return false;
        if (!IsAllowed(principal, oidc))
            return false;
        identity.AddClaim(new Claim(identity.RoleClaimType, GatewayAuth.AdminRole));
        identity.AddClaim(new Claim(SessionClaim, "oidc"));
        return true;
    }

    public static bool IsAllowed(ClaimsPrincipal principal, OidcOptions oidc)
        => oidc.AllowedGroups.Count > 0 &&
           principal.FindAll(oidc.GroupClaim).Any(c => oidc.AllowedGroups.Contains(c.Value, StringComparer.Ordinal));

    public static bool IsBrowserSession(ClaimsPrincipal user) => user.HasClaim(c => c.Type == SessionClaim);
}

public sealed class AdminGroupRequiredException : Exception
{
    public AdminGroupRequiredException() : base("The user is not in an allowed admin group.") { }
}

/// <summary>
/// Admin API calls authenticated by a browser session (not the admin key) must carry an antiforgery token for unsafe methods, so a
/// page on another site (or another grifball.xyz subdomain, which SameSite=Lax treats as the same site) cannot act with the cookie.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ValidateAntiforgeryForBrowserSessionAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || HttpMethods.IsOptions(http.Request.Method))
            return;
        if (!AdminSession.IsBrowserSession(http.User))
            return;
        try
        {
            await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new BadRequestObjectResult(new { message = "Missing or invalid antiforgery token." });
        }
    }
}

/// <summary>HTML for the admin page and its messages: encoded output only, no scripts, strict security headers.</summary>
public static class AdminPageHtml
{
    public static void ApplySecurityHeaders(HttpResponse response)
    {
        var h = response.Headers;
        // Forms post to this host only; the sign-in form's redirect continues to Microsoft (form-action also covers redirects).
        h.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src 'self'; form-action 'self' https:; frame-ancestors 'none'; base-uri 'none'";
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h.CacheControl = "no-store";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
    }

    public static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

    public static string Document(string title, string body)
        => "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
           $"<title>Halo gateway: {E(title)}</title><style>{Style}</style></head><body><main><h1>{E(title)}</h1>{body}</main></body></html>";

    public static string Message(string title, string message, params (string Text, string Href)[] links)
        => Document(title, $"<p>{E(message)}</p>" + string.Concat(links.Select(l => $"<p><a href=\"{E(l.Href)}\">{E(l.Text)}</a></p>")));

    private const string Style =
        "body{font-family:system-ui,sans-serif;margin:0;background:#f6f7f9;color:#1b1f24}" +
        "main{max-width:44em;margin:2em auto;padding:0 16px}" +
        "table{border-collapse:collapse;width:100%;background:#fff}th,td{text-align:left;padding:.45em .6em;border-bottom:1px solid #e3e6ea;vertical-align:top}" +
        "th{width:40%;font-weight:600}form{display:inline-block;margin:.8em .6em 0 0}" +
        "button{font:inherit;padding:.5em 1em;border-radius:6px;border:1px solid #9aa4b1;background:#fff;cursor:pointer}" +
        "button.primary{background:#1f6feb;border-color:#1f6feb;color:#fff}button.danger{background:#cf222e;border-color:#cf222e;color:#fff}" +
        ".note{padding:.6em .8em;border-radius:6px;background:#ddf4ff;border:1px solid #54aeff}.note.bad{background:#ffebe9;border-color:#ff8182}" +
        ".ok{color:#1a7f37;font-weight:600}.bad{color:#cf222e;font-weight:600}.muted{color:#57606a}" +
        "@media (prefers-color-scheme:dark){body{background:#0d1117;color:#e6edf3}table{background:#161b22}th,td{border-color:#30363d}" +
        "button{background:#21262d;color:#e6edf3;border-color:#30363d}.note{background:#0c2d6b;border-color:#1f6feb}.note.bad{background:#490202;border-color:#cf222e}" +
        ".muted{color:#8b949e}a{color:#58a6ff}}";
}
