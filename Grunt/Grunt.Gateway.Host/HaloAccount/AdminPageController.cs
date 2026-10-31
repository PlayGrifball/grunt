using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Surprenant.Grunt.Gateway.Host.Auth;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using static Surprenant.Grunt.Gateway.Host.Auth.AdminPageHtml;

namespace Surprenant.Grunt.Gateway.Host.HaloAccount;

/// <summary>
/// The browser admin page (<c>/admin</c>): the Halo account's status, and sign in / refresh / clear tokens, behind an OpenID Connect
/// session (<see cref="AdminSession"/>) restricted to <see cref="OidcOptions.AllowedGroups"/>. Server-rendered, no scripts; every POST
/// carries an antiforgery token and answers with a redirect back to the page. Not found while <c>Gateway:Oidc</c> is not configured.
/// </summary>
[ApiExplorerSettings(IgnoreApi = true)]
[Route("admin")]
[AllowAnonymous] // authentication is checked per action, against the session cookie only (never the API's default scheme)
public sealed class AdminPageController : ControllerBase
{
    /// <summary>Outcome banners after a POST (a fixed list: nothing from the query string is echoed).</summary>
    private static readonly Dictionary<string, (string Text, bool Bad)> Outcomes = new(StringComparer.Ordinal)
    {
        ["signed-in"] = ("Signed in. The gateway can call Halo now.", false),
        ["signin-failed"] = ("The Microsoft sign-in did not complete; see the last error below.", true),
        ["refreshed"] = ("Token refreshed.", false),
        ["refresh-failed"] = ("The refresh failed; see the last error below.", true),
        ["cleared"] = ("Tokens cleared. Sign in again to use the gateway.", false),
    };

    private readonly HaloAccountService _account;
    private readonly IAntiforgery _antiforgery;
    private readonly IOptionsMonitor<GatewayOptions> _options;

    public AdminPageController(HaloAccountService account, IAntiforgery antiforgery, IOptionsMonitor<GatewayOptions> options)
    {
        _account = account;
        _antiforgery = antiforgery;
        _options = options;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] string? done)
    {
        if (await RequireSessionAsync() is { } denied)
            return denied;

        var s = _account.Status();
        var token = Token();
        var html = new StringBuilder();
        if (done is not null && Outcomes.TryGetValue(done, out var outcome))
            html.Append(CultureInfo.InvariantCulture, $"<p class=\"note{(outcome.Bad ? " bad" : "")}\">{E(outcome.Text)}</p>");

        var ok = s.Status == HaloAuthStatus.Authorized;
        html.Append("<table>");
        Row(html, "Halo account", $"<span class=\"{(ok ? "ok" : "bad")}\">{E(StatusText(s.Status))}</span>");
        Row(html, "Reason", E(s.Reason));
        Row(html, "Token stored", s.TokenStored ? "Yes" : "No");
        Row(html, "Last refresh", When(s.LastRefreshUtc));
        Row(html, "Access token expires", When(s.AccessTokenExpiresUtc));
        Row(html, "Spartan token expires", When(s.SpartanTokenExpiresUtc));
        Row(html, "Last error", s.LastErrorUtc is null ? "<span class=\"muted\">none</span>" : $"{When(s.LastErrorUtc)}<br>{E(s.LastError)}");
        Row(html, "Consecutive failures", s.ConsecutiveFailures.ToString(CultureInfo.InvariantCulture));
        html.Append("</table>");

        html.Append(Form("admin/signin", token, "<button class=\"primary\" type=\"submit\">Sign in with Microsoft</button>"));
        html.Append(Form("admin/refresh", token, "<button type=\"submit\">Refresh now</button>"));
        html.Append("<form method=\"get\" action=\"/admin/clear\"><button class=\"danger\" type=\"submit\">Sign out / clear tokens</button></form>");
        html.Append(CultureInfo.InvariantCulture, $"<p class=\"muted\">Signed in to this page as {E(User.Identity?.Name ?? User.FindFirstValue("sub"))}.</p>");
        html.Append(Form("admin/logout", token, "<button type=\"submit\">Log out of this page</button>"));
        return Html(200, "Halo account", html.ToString());
    }

    /// <summary>Starts the Microsoft sign-in; the callback (<c>/oauth/callback</c>) comes back here.</summary>
    [HttpPost("signin")]
    public async Task<IActionResult> SignIn()
    {
        if (await RequireSessionAsync(validateForm: true) is { } denied)
            return denied;
        return Redirect(_account.CreateSignInUrl(returnToAdminPage: true));
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        if (await RequireSessionAsync(validateForm: true) is { } denied)
            return denied;
        var refreshed = await _account.RefreshAsync();
        return BackToPage(refreshed ? "refreshed" : "refresh-failed");
    }

    /// <summary>Confirmation step before clearing the tokens.</summary>
    [HttpGet("clear")]
    public async Task<IActionResult> ConfirmClear()
    {
        if (await RequireSessionAsync() is { } denied)
            return denied;
        var body = "<p>This deletes the stored Halo tokens. Every consumer gets errors until someone signs the account in again.</p>" +
                   Form("admin/clear", Token(), "<button class=\"danger\" type=\"submit\">Yes, clear the tokens</button>") +
                   "<p><a href=\"/admin\">Cancel</a></p>";
        return Html(200, "Clear the Halo tokens?", body);
    }

    [HttpPost("clear")]
    public async Task<IActionResult> Clear()
    {
        if (await RequireSessionAsync(validateForm: true) is { } denied)
            return denied;
        await _account.ClearAsync();
        return BackToPage("cleared");
    }

    /// <summary>Ends this page's session (not the identity provider's).</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        if (await RequireSessionAsync(validateForm: true) is { } denied)
            return denied;
        await HttpContext.SignOutAsync(AdminSession.CookieScheme);
        return Html(200, "Logged out", "<p><a href=\"/admin\">Sign in again</a></p>");
    }

    /// <summary>
    /// Null when the request has an admin session (and, for POSTs, a valid antiforgery token); otherwise the response to send: not found
    /// without OIDC, a sign-in challenge without a session, 403 without the admin group, 400 for a bad token.
    /// </summary>
    private async Task<IActionResult?> RequireSessionAsync(bool validateForm = false)
    {
        AdminPageHtml.ApplySecurityHeaders(Response);
        if (!_options.CurrentValue.Oidc.Enabled)
            return Html(404, "Not configured", "<p>Browser sign-in is not configured (Gateway:Oidc). Use the admin API with the admin key.</p>");

        var result = await HttpContext.AuthenticateAsync(AdminSession.CookieScheme);
        if (!result.Succeeded)
        {
            if (validateForm)
                return Html(401, "Session expired", "<p><a href=\"/admin\">Sign in again</a></p>");
            return Challenge(new AuthenticationProperties { RedirectUri = "/admin" }, AdminSession.OidcScheme);
        }

        HttpContext.User = result.Principal!;
        if (!User.IsInRole(GatewayAuth.AdminRole))
            return Html(403, "Not allowed", "<p>Your account is not in a group allowed to manage the Halo gateway.</p>");

        if (validateForm)
        {
            try
            {
                await _antiforgery.ValidateRequestAsync(HttpContext);
            }
            catch (AntiforgeryValidationException)
            {
                return Html(400, "Form expired", "<p>The form expired or did not come from this page. <a href=\"/admin\">Reload the admin page</a> and try again.</p>");
            }
        }
        return null;
    }

    private AntiforgeryTokenSet Token() => _antiforgery.GetAndStoreTokens(HttpContext);

    private RedirectResult BackToPage(string outcome) => new("/admin?done=" + outcome);

    private ContentResult Html(int status, string title, string body) => new()
    {
        StatusCode = status,
        ContentType = "text/html; charset=utf-8",
        Content = Document(title, body),
    };

    private static string Form(string action, AntiforgeryTokenSet token, string button)
        => $"<form method=\"post\" action=\"/{action}\"><input type=\"hidden\" name=\"{E(token.FormFieldName)}\" value=\"{E(token.RequestToken)}\">{button}</form>";

    private static void Row(StringBuilder html, string name, string valueHtml)
        => html.Append(CultureInfo.InvariantCulture, $"<tr><th>{E(name)}</th><td>{valueHtml}</td></tr>");

    private static string When(DateTimeOffset? t)
        => t is { } v ? E(v.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)) : "<span class=\"muted\">never</span>";

    private static string StatusText(HaloAuthStatus status) => status switch
    {
        HaloAuthStatus.Authorized => "Signed in",
        HaloAuthStatus.ReauthRequired => "Sign-in required",
        HaloAuthStatus.Error => "Error (retrying)",
        _ => "Not checked yet",
    };
}
