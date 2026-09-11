using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// A sign-in page can sit open while the session behind it ends. ASP.NET Core binds an antiforgery
/// token to the identity that rendered it, so such a page used to post a token that no longer
/// matched the now-anonymous request and failed with a bare 400 the operator had to know to refresh
/// out of. These pin both halves of the fix: the page is never rendered for somebody already signed
/// in, and a token that has gone stale anyway returns to a usable page instead of a dead end.
/// </summary>
public sealed partial class ApplicationTests
{
    private static HttpClient LoginClient(MonkeysphereApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });

    private static FormUrlEncodedContent LoginForm(string token, string returnUrl = "/") =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["username"] = "admin",
            ["password"] = AdministratorPassword,
            ["returnUrl"] = returnUrl,
        });

    [Fact]
    public async Task TheSignInPageIsNotRenderedForSomebodyAlreadySignedIn()
    {
        await using MonkeysphereApplicationFactory factory = new();
        using HttpClient client = LoginClient(factory);

        string token = ExtractAntiforgeryToken(await client.GetStringAsync("/login"));
        using FormUrlEncodedContent form = LoginForm(token);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/auth/login", form)).StatusCode);

        // Signed in, the page sends the operator on rather than rendering a form whose token would
        // be bound to their identity and would stop validating the moment the session ended.
        HttpResponseMessage signedIn = await client.GetAsync("/login");
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.DoesNotContain("/login", signedIn.Headers.Location!.ToString(), StringComparison.Ordinal);

        // Somebody not signed in still gets the form, which is the whole point of the page.
        using HttpClient anonymous = LoginClient(factory);
        Assert.Contains("name=\"password\"", await anonymous.GetStringAsync("/login"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStaleSignInPageReturnsAUsablePageRatherThanABareBadRequest()
    {
        await using MonkeysphereApplicationFactory factory = new();
        using HttpClient client = LoginClient(factory);

        string token = ExtractAntiforgeryToken(await client.GetStringAsync("/login"));

        // The token no longer matches: the page has been open across a change of session. Posting a
        // token from a different page is the same condition and is what this reproduces.
        using FormUrlEncodedContent stale = LoginForm(token[..^4] + "AAAA", "/records");
        HttpResponseMessage response = await client.PostAsync("/auth/login", stale);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        string location = response.Headers.Location!.ToString();
        Assert.StartsWith("/login?error=2", location, StringComparison.Ordinal);
        // Where they were heading survives, so the retry still lands where they meant to go.
        Assert.Contains("returnUrl=%2Frecords", location, StringComparison.Ordinal);

        // The page that comes back explains itself and carries a token that works.
        string recovered = await client.GetStringAsync(location);
        Assert.Contains("open too long", recovered, StringComparison.Ordinal);
        using FormUrlEncodedContent retry = LoginForm(ExtractAntiforgeryToken(recovered), "/records");
        HttpResponseMessage second = await client.PostAsync("/auth/login", retry);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.Equal("/records", second.Headers.Location?.ToString());
    }

    // A forged sign-in must still not sign anybody in; it is redirected, not honoured.
    [Fact]
    public async Task AStaleSignInPageDoesNotSignAnybodyIn()
    {
        await using MonkeysphereApplicationFactory factory = new();
        using HttpClient client = LoginClient(factory);

        string token = ExtractAntiforgeryToken(await client.GetStringAsync("/login"));
        using FormUrlEncodedContent stale = LoginForm(token[..^4] + "AAAA");
        _ = await client.PostAsync("/auth/login", stale);

        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/records")).StatusCode);
    }
}
