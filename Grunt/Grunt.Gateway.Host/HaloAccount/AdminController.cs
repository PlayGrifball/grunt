using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Surprenant.Grunt.Gateway.Host.Auth;
using System.Net;

namespace Surprenant.Grunt.Gateway.Host.HaloAccount;

/// <summary>Halo account administration; every action needs the admin key (header <c>X-Admin-Key</c>).</summary>
[ApiController]
[Route("admin/halo")]
[Authorize(Policy = GatewayAuth.AdminPolicy)]
public sealed class AdminController : ControllerBase
{
    private readonly HaloAccountService _account;

    public AdminController(HaloAccountService account) => _account = account;

    /// <summary>Authorized?, token expiries, last refresh and last error.</summary>
    [HttpGet("status")]
    public HaloAuthSnapshot Status() => _account.Status();

    /// <summary>A Microsoft sign-in URL (one-time state); <c>?redirect=true</c> answers with a 302 to it instead.</summary>
    [HttpGet("signin")]
    public IActionResult SignIn([FromQuery] bool redirect = false)
    {
        var url = _account.CreateSignInUrl();
        return redirect ? Redirect(url) : Ok(new { url });
    }

    public sealed record CodeRequest(string Code);

    /// <summary>Fallback: paste the <c>code</c> query value from the redirect URL when the callback could not be reached.</summary>
    [HttpPost("code")]
    public async Task<IActionResult> Code([FromBody] CodeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
            return BadRequest(new { message = "Missing code." });
        var code = request.Code.Contains("code=", StringComparison.Ordinal)
            ? ExtractCode(request.Code) ?? request.Code
            : request.Code;
        var result = await _account.RedeemCodeAsync(code);
        return result.Success ? Ok(result) : UnprocessableEntity(result);
    }

    /// <summary>Refresh the OAuth token now.</summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        var ok = await _account.RefreshAsync();
        return ok ? Ok(_account.Status()) : UnprocessableEntity(_account.Status());
    }

    /// <summary>Clear stored tokens and force a new sign-in.</summary>
    [HttpDelete("tokens")]
    public async Task<IActionResult> ClearTokens()
    {
        await _account.ClearAsync();
        return Ok(_account.Status());
    }

    internal static string? ExtractCode(string urlOrQuery)
    {
        var query = urlOrQuery.Contains('?') ? urlOrQuery[(urlOrQuery.IndexOf('?') + 1)..] : urlOrQuery;
        foreach (var part in query.Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "code")
                return WebUtility.UrlDecode(kv[1]);
        }
        return null;
    }
}

/// <summary>
/// The gateway's single OAuth redirect URI (<c>/oauth/callback</c>, register it on the Microsoft app). Anonymous because the browser
/// arrives from Microsoft, but only codes carrying a state issued by <c>GET /admin/halo/signin</c> are redeemed.
/// </summary>
[ApiController]
[Route("oauth/callback")]
[AllowAnonymous]
public sealed class OAuthCallbackController : ControllerBase
{
    private readonly HaloAccountService _account;

    public OAuthCallbackController(HaloAccountService account) => _account = account;

    [HttpGet]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription)
    {
        if (!string.IsNullOrEmpty(error))
            return Page(400, "Sign-in failed", $"Microsoft returned {error}: {errorDescription}");
        if (!_account.ConsumeState(state))
            return Page(400, "Sign-in link expired", "This sign-in link is unknown, used or expired. Get a new one from GET /admin/halo/signin.");
        if (string.IsNullOrEmpty(code))
            return Page(400, "Sign-in failed", "Microsoft did not return a code.");

        var result = await _account.RedeemCodeAsync(code);
        return Page(result.Success ? 200 : 502, result.Success ? "Signed in" : "Sign-in failed", result.Message);
    }

    private ContentResult Page(int status, string title, string message) => new()
    {
        StatusCode = status,
        ContentType = "text/html; charset=utf-8",
        Content = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Halo gateway: {WebUtility.HtmlEncode(title)}</title></head>" +
                  $"<body style=\"font-family:sans-serif;max-width:40em;margin:4em auto\"><h1>{WebUtility.HtmlEncode(title)}</h1>" +
                  $"<p>{WebUtility.HtmlEncode(message)}</p><p>You can close this tab.</p></body></html>",
    };
}
