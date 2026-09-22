using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Net.Http.Headers;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// SessionLifetime is only worth configuring if the cookie the sign-in actually issues obeys it.
/// It did not: the sign-in set an ExpiresUtc of its own, which replaces the handler's configured
/// ExpireTimeSpan outright and which sliding expiration then renews at that same shorter length
/// forever, so the configured idle timeout never reached the browser at all. Every page here is an
/// interactive Blazor circuit, so an established session makes almost no further HTTP requests and
/// has few chances to slide — which is what turned the mismatch into repeated signing out.
/// </summary>
public sealed class SessionCookieLifetimeTests
{
    private const string AdministratorPassword = "test-only-LongPassword-2048!";

    [Fact]
    public async Task TheSessionCookieLastsAsLongAsTheConfiguredIdleTimeout()
    {
        await using ConfiguredSessionApplicationFactory factory = new(TimeSpan.FromDays(3));
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });

        string token = ExtractAntiforgeryToken(await client.GetStringAsync("/login"));
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["username"] = "admin",
            ["password"] = AdministratorPassword,
            ["returnUrl"] = "/",
        });

        DateTimeOffset signedInAt = DateTimeOffset.UtcNow;
        HttpResponseMessage login = await client.PostAsync("/auth/login", form);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        SetCookieHeaderValue cookie = SetCookieHeaderValue.Parse(
            Assert.Single(login.Headers.GetValues("Set-Cookie"), value =>
                value.StartsWith("Monkeysphere.Session=", StringComparison.Ordinal)));

        // An expiry at all means the cookie is persistent: an idle timeout measured in days cannot
        // survive a browser restart otherwise, however long the ticket inside the cookie is good for.
        Assert.True(cookie.Expires.HasValue, "The session cookie was not persistent: it carried no expiry.");
        TimeSpan lasts = cookie.Expires.Value - signedInAt;
        Assert.True(
            lasts > TimeSpan.FromDays(2.9) && lasts < TimeSpan.FromDays(3.1),
            $"Expected the configured three-day idle timeout to reach the browser, but the cookie lasts {lasts}.");
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        int name = html.IndexOf("name=\"__RequestVerificationToken\"", StringComparison.Ordinal);
        Assert.True(name >= 0, "The sign-in page rendered no antiforgery token.");
        int value = html.IndexOf("value=\"", name, StringComparison.Ordinal) + "value=\"".Length;
        return html[value..html.IndexOf('"', value)];
    }

    private sealed class ConfiguredSessionApplicationFactory(TimeSpan idleTimeout) : MonkeysphereApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // A host setting rather than layered application configuration, because the cookie
            // options are built from the builder's own configuration while the host is being built.
            builder.UseSetting(
                "Monkeysphere:Session:IdleTimeoutMinutes",
                idleTimeout.TotalMinutes.ToString(CultureInfo.InvariantCulture));
        }
    }
}
