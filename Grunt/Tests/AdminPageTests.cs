using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NSubstitute;
using Surprenant.Grunt.Core;
using Surprenant.Grunt.Gateway.Host;
using Surprenant.Grunt.Gateway.Host.Auth;
using Surprenant.Grunt.Gateway.Host.HaloAccount;
using Surprenant.Grunt.Models;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace Tests;

/// <summary>
/// The browser admin page and its OpenID Connect session. The cookie scheme is replaced by <see cref="FakeSessionHandler"/> (a session
/// is whatever the test's X-Test-Session header says, as if the cookie held it); the OIDC handler itself is real, with a static
/// configuration instead of a discovery document.
/// </summary>
public class AdminPageTests
{
    private const string AdminKey = "admin-key";
    private const string SessionHeader = "X-Test-Session";

    /// <summary>Test stand-in for the session cookie: "admin" is a granted session, "user" a session without the admin group.</summary>
    private sealed class FakeSessionHandler : SignInAuthenticationHandler<CookieAuthenticationOptions>
    {
        public FakeSessionHandler(IOptionsMonitor<CookieAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var session = Request.Headers[SessionHeader].ToString();
            if (string.IsNullOrEmpty(session))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity(new[] { new Claim("sub", "u-" + session), new Claim("preferred_username", session) },
                "test", "preferred_username", ClaimTypes.Role);
            if (session == "admin")
                identity.AddClaims(new[] { new Claim(ClaimTypes.Role, GatewayAuth.AdminRole), new Claim(AdminSession.SessionClaim, "oidc") });
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }

        protected override Task HandleSignInAsync(ClaimsPrincipal user, AuthenticationProperties? properties) => Task.CompletedTask;

        protected override Task HandleSignOutAsync(AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly bool _oidc;

        public Factory(bool oidc) => _oidc = oidc;

        public IHaloOAuthClient OAuth { get; } = Substitute.For<IHaloOAuthClient>();
        public IHaloSession Session { get; } = Substitute.For<IHaloSession>();
        public string TokenDirectory { get; } = Path.Combine(Path.GetTempPath(), "grunt-admin-page-tests", Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Gateway:AdminKey", AdminKey);
            builder.UseSetting("Gateway:Auth:UseInstanceLock", "false");
            builder.UseSetting("Gateway:Auth:InitialDelay", "01:00:00");
            builder.UseSetting("Gateway:Auth:TokenPath", Path.Combine(TokenDirectory, "tokens.json"));
            if (_oidc)
            {
                builder.UseSetting("Gateway:Oidc:Authority", "https://idp.test/application/o/halo-gateway/");
                builder.UseSetting("Gateway:Oidc:ClientId", "halo-gateway");
                builder.UseSetting("Gateway:Oidc:ClientSecret", "secret");
                builder.UseSetting("Gateway:Oidc:AllowedGroups:0", "admins");
            }
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(Substitute.For<IHaloInfiniteClient>());
                services.AddSingleton(OAuth);
                services.AddSingleton(Session);
                if (!_oidc)
                    return;
                services.Configure<AuthenticationOptions>(o =>
                {
                    foreach (var scheme in o.Schemes.Where(s => s.Name == AdminSession.CookieScheme))
                        scheme.HandlerType = typeof(FakeSessionHandler);
                });
                services.Configure<OpenIdConnectOptions>(AdminSession.OidcScheme, o => o.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = "https://idp.test/application/o/halo-gateway/",
                    AuthorizationEndpoint = "https://idp.test/application/o/authorize/",
                    TokenEndpoint = "https://idp.test/application/o/token/",
                });
            });
        }

        public HttpClient Browser(string? session = null)
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            if (session is not null)
                client.DefaultRequestHeaders.Add(SessionHeader, session);
            return client;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Directory.Exists(TokenDirectory))
                Directory.Delete(TokenDirectory, recursive: true);
        }
    }

    private static readonly Regex FormToken = new("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"", RegexOptions.Compiled);

    private static async Task<string> TokenFrom(HttpResponseMessage page)
    {
        var html = await page.Content.ReadAsStringAsync();
        var match = FormToken.Match(html);
        Assert.That(match.Success, Is.True, "the page should carry an antiforgery token");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static FormUrlEncodedContent Form(string token)
        => new(new Dictionary<string, string> { ["__RequestVerificationToken"] = token });

    [Test]
    public async Task NotFoundWithoutOidc()
    {
        using var factory = new Factory(oidc: false);

        var page = await factory.Browser().GetAsync("admin");

        Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task WithoutASessionRedirectsToTheIdentityProviderWithPkce()
    {
        using var factory = new Factory(oidc: true);

        var page = await factory.Browser().GetAsync("admin");

        Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        var location = page.Headers.Location!.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(location, Does.StartWith("https://idp.test/application/o/authorize/"));
            Assert.That(location, Does.Contain("response_type=code"));
            Assert.That(location, Does.Contain("code_challenge_method=S256"));
            Assert.That(location, Does.Contain("client_id=halo-gateway"));
            Assert.That(location, Does.Contain(Uri.EscapeDataString("/admin/signin-oidc")));
        });
    }

    [Test]
    public async Task ProxyIdentityHeadersAreNotTrusted()
    {
        using var factory = new Factory(oidc: true);
        var client = factory.Browser();
        client.DefaultRequestHeaders.Add("X-Forwarded-User", "admin");
        client.DefaultRequestHeaders.Add("Remote-User", "admin");
        client.DefaultRequestHeaders.Add("X-authentik-username", "admin");
        client.DefaultRequestHeaders.Add("X-authentik-groups", "admins");

        var page = await client.GetAsync("admin");
        var api = await client.GetAsync("admin/halo/status");

        Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(api.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task SessionWithoutTheAdminGroupIsForbidden()
    {
        using var factory = new Factory(oidc: true);

        var page = await factory.Browser("user").GetAsync("admin");
        var api = await factory.Browser("user").GetAsync("admin/halo/status");

        Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(api.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task AdminSessionSeesStatusWithSecurityHeaders()
    {
        using var factory = new Factory(oidc: true);

        var page = await factory.Browser("admin").GetAsync("admin");
        var html = await page.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain("Halo account").And.Contain("Sign in with Microsoft").And.Contain("Refresh now").And.Contain("clear tokens"));
            Assert.That(page.Headers.GetValues("Content-Security-Policy").Single(), Does.Contain("frame-ancestors 'none'"));
            Assert.That(page.Headers.GetValues("X-Frame-Options").Single(), Is.EqualTo("DENY"));
            Assert.That(page.Headers.GetValues("X-Content-Type-Options").Single(), Is.EqualTo("nosniff"));
            Assert.That(page.Headers.CacheControl?.NoStore, Is.True);
        });
    }

    [Test]
    public async Task PostsNeedTheAntiforgeryToken()
    {
        using var factory = new Factory(oidc: true);
        var browser = factory.Browser("admin");

        var withoutToken = await browser.PostAsync("admin/refresh", Form("forged"));

        Assert.That(withoutToken.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        await factory.OAuth.DidNotReceive().RefreshAsync(Arg.Any<string>());
    }

    [Test]
    public async Task SignInFromThePageComesBackToThePage()
    {
        using var factory = new Factory(oidc: true);
        string? issuedState = null;
        factory.OAuth.BuildSignInUrl(Arg.Do<string>(s => issuedState = s)).Returns(c => "https://login.live.com/oauth20_authorize.srf?state=" + c.Arg<string>());
        factory.OAuth.RedeemCodeAsync("the-code").Returns(new OAuthToken { AccessToken = "a", RefreshToken = "r", ExpiresIn = 3600 });
        var browser = factory.Browser("admin");
        var token = await TokenFrom(await browser.GetAsync("admin"));

        var start = await browser.PostAsync("admin/signin", Form(token));
        var callback = await factory.Browser().GetAsync($"oauth/callback?code=the-code&state={issuedState}");
        var page = await (await browser.GetAsync(callback.Headers.Location!.ToString())).Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(start.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(start.Headers.Location!.Host, Is.EqualTo("login.live.com"));
            Assert.That(callback.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(callback.Headers.Location!.ToString(), Is.EqualTo("/admin?done=signed-in"));
            Assert.That(page, Does.Contain("Signed in. The gateway can call Halo now.").And.Contain("Signed in</span>"));
        });
    }

    [Test]
    public async Task RefreshAndClearWithConfirmation()
    {
        using var factory = new Factory(oidc: true);
        var browser = factory.Browser("admin");
        var token = await TokenFrom(await browser.GetAsync("admin"));

        var refresh = await browser.PostAsync("admin/refresh", Form(token));
        var confirm = await browser.GetAsync("admin/clear");
        var confirmHtml = await confirm.Content.ReadAsStringAsync();
        var clear = await browser.PostAsync("admin/clear", Form(await TokenFrom(confirm)));

        Assert.Multiple(() =>
        {
            // Nothing stored yet: the refresh reports that a sign-in is needed.
            Assert.That(refresh.Headers.Location!.ToString(), Is.EqualTo("/admin?done=refresh-failed"));
            Assert.That(confirmHtml, Does.Contain("Yes, clear the tokens"));
            Assert.That(clear.Headers.Location!.ToString(), Is.EqualTo("/admin?done=cleared"));
        });
        await factory.Session.Received().ResetAsync();
    }

    [Test]
    public async Task AdminApiAcceptsASessionButUnsafeCallsNeedTheToken()
    {
        using var factory = new Factory(oidc: true);
        var browser = factory.Browser("admin");
        var keyed = factory.Browser();
        keyed.DefaultRequestHeaders.Add("X-Admin-Key", AdminKey);

        var status = await browser.GetAsync("admin/halo/status");
        var deleteWithoutToken = await browser.DeleteAsync("admin/halo/tokens");
        var deleteWithKey = await keyed.DeleteAsync("admin/halo/tokens");

        Assert.Multiple(() =>
        {
            Assert.That(status.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(deleteWithoutToken.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(deleteWithKey.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        });
    }

    [Test]
    public void OnlyAllowedGroupsGetTheAdminRole()
    {
        var oidc = new OidcOptions { AllowedGroups = { "admins" } };
        ClaimsPrincipal User(params string[] groups)
            => new(new ClaimsIdentity(groups.Select(g => new Claim("groups", g)).Append(new Claim("sub", "x")), "oidc", "sub", ClaimTypes.Role));

        var admin = User("users", "admins");
        var other = User("users", "Admins");
        var none = User();

        Assert.Multiple(() =>
        {
            Assert.That(AdminSession.TryGrantAdmin(admin, oidc), Is.True);
            Assert.That(admin.IsInRole(GatewayAuth.AdminRole), Is.True);
            Assert.That(AdminSession.IsBrowserSession(admin), Is.True);
            Assert.That(AdminSession.TryGrantAdmin(other, oidc), Is.False);
            Assert.That(AdminSession.TryGrantAdmin(none, oidc), Is.False);
            Assert.That(AdminSession.TryGrantAdmin(User("admins"), new OidcOptions()), Is.False, "no allowed groups: nobody");
            Assert.That(AdminSession.TryGrantAdmin(null, oidc), Is.False);
        });
    }
}
