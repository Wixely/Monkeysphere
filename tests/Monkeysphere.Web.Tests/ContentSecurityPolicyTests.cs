using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// The policy is the one piece of this application whose failures are silent in both directions: a
/// directive that is too tight breaks a page in a way only a browser console mentions, and one that
/// is too loose changes nothing visible at all. So the shape of it is pinned here rather than left
/// to be re-read out of a header by hand — including the two parts easiest to widen by accident,
/// the single inline script and the single external host.
/// </summary>
public sealed class ContentSecurityPolicyTests
{
    private const string AdministratorPassword = "test-only-LongPassword-2048!";

    /// <summary>
    /// The class Cytoscape puts on the graph container, and the rule it inserts for it. Written out
    /// here so the hash below is derived from the same two pieces the library is asserted to still
    /// contain, rather than from a value copied out of a browser's suggestion once.
    /// </summary>
    private const string CytoscapeContainerClass = "__________cytoscape_container";

    private const string CytoscapeRuleSuffix = " { position: relative; }";

    [Fact]
    public async Task TheDocumentsOneInlineScriptIsAllowedByANonceThatIsNewEveryRequest()
    {
        await using MonkeysphereApplicationFactory factory = new();
        using HttpClient client = await SignedInAsync(factory);

        using HttpResponseMessage first = await client.GetAsync("/");
        string firstHtml = await first.Content.ReadAsStringAsync();
        string firstPolicy = Policy(first);
        string firstNonce = Nonce(firstPolicy);

        // The header allowed a value, and every script the document carries inline carries that same
        // value. A mismatch is not a subtle regression: the framework never boots and every page is
        // inert. The list is normally one long — the framework's import map, the only script this
        // application does not serve as a file of its own — but it is empty in a host that serves
        // assets unfingerprinted, because then there is nothing for an import map to map. So this
        // asserts the rule rather than the count, and the count is bounded separately below.
        Assert.Contains($"script-src 'self' 'nonce-{firstNonce}'", firstPolicy, StringComparison.Ordinal);
        List<string> inline = InlineScriptTags(firstHtml);
        foreach (string tag in inline)
        {
            Assert.Contains($"nonce=\"{firstNonce}\"", tag, StringComparison.Ordinal);
        }

        // An inline script nobody accounted for is the thing a nonce cannot save you from, because
        // whoever adds it will reach for the nonce and the policy will let it through. Keeping the
        // number at the one known script means a second one has to be argued for here first.
        Assert.True(inline.Count <= 1, $"The document carries {inline.Count} inline scripts: {string.Join(" ", inline)}");

        // No escape hatch beside it. 'unsafe-eval' is worth naming on its own: Blazor Server runs
        // its components on the server and has never needed it.
        Assert.DoesNotContain("'unsafe-inline'", ScriptDirective(firstPolicy), StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-eval'", firstPolicy, StringComparison.Ordinal);

        using HttpResponseMessage second = await client.GetAsync("/");
        string secondNonce = Nonce(Policy(second));

        // The point of a nonce rather than a hash is that it is worth nothing once the response it
        // was minted for is over, so a script injected into one page cannot carry a value some
        // other page's header will accept.
        Assert.NotEqual(firstNonce, secondNonce);
        foreach (string tag in InlineScriptTags(await second.Content.ReadAsStringAsync()))
        {
            Assert.Contains($"nonce=\"{secondNonce}\"", tag, StringComparison.Ordinal);
        }

        // Anything not named falls back to default-src, so that is 'self' rather than absent: a
        // directive nobody thought of should narrow what the browser may do, not leave it open.
        Assert.StartsWith("default-src 'self'; ", firstPolicy, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", firstPolicy, StringComparison.Ordinal);
        Assert.Contains("frame-src 'none'", firstPolicy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", firstPolicy, StringComparison.Ordinal);
        Assert.Contains("form-action 'self'", firstPolicy, StringComparison.Ordinal);
        Assert.Contains("base-uri 'self'", firstPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTileHostIsNamedOnlyWhileAnOperatorHasTurnedExternalTilesOn()
    {
        const string tileHost = "https://tile.openstreetmap.org";

        await using MonkeysphereApplicationFactory factory = new();
        using HttpClient client = await SignedInAsync(factory);

        using HttpResponseMessage closed = await client.GetAsync("/map");
        string closedPolicy = Policy(closed);

        // A deployment that has not opted in never names the host at all, which is the reason this
        // is decided per response instead of allowed permanently because one page might want it.
        Assert.DoesNotContain(tileHost, closedPolicy, StringComparison.Ordinal);
        Assert.Contains("img-src 'self' data: blob:;", closedPolicy, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", closedPolicy, StringComparison.Ordinal);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            _ = await scope.ServiceProvider.GetRequiredService<IMapSettingsService>()
                .SaveAsync(new(ExternalTilesEnabled: true));
        }

        using HttpResponseMessage opened = await client.GetAsync("/map");
        string openedPolicy = Policy(opened);

        // Both directives, because a tile arrives as an image but the library asks for it with
        // crossOrigin set, and a deployment that allowed only one of the two would break on a
        // browser that accounted for it the other way.
        Assert.Contains($"img-src 'self' data: blob: {tileHost};", openedPolicy, StringComparison.Ordinal);
        Assert.Contains($"connect-src 'self' {tileHost}", openedPolicy, StringComparison.Ordinal);

        // Turning tiles on loosens nothing else, and in particular does not let a script or a
        // stylesheet come from the tile host.
        Assert.Contains("script-src 'self' 'nonce-", openedPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain(tileHost, ScriptDirective(openedPolicy), StringComparison.Ordinal);
        Assert.DoesNotContain(tileHost, StyleDirective(openedPolicy), StringComparison.Ordinal);

        // A static asset is not a document and cannot ask for a tile, so it is answered from the
        // fixed policy without a database read — and therefore still names no external host.
        using HttpResponseMessage asset = await client.GetAsync("/app.css");
        Assert.DoesNotContain(tileHost, Policy(asset), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOneStylesheetTheGraphLibraryInsertsIsAllowedByItsOwnHashAndNothingWider()
    {
        await using MonkeysphereApplicationFactory factory = new();
        using HttpClient client = await SignedInAsync(factory);

        // Read back what is actually served rather than a copy of it, so an upgrade of the vendored
        // file is the thing this notices.
        string library = await client.GetStringAsync("/vendor/cytoscape/3.34.0/cytoscape.min.js");
        Assert.Contains($"\"{CytoscapeContainerClass}\"", library, StringComparison.Ordinal);
        Assert.Contains($"\"{CytoscapeRuleSuffix}\"", library, StringComparison.Ordinal);

        string expected = Convert.ToBase64String(SHA256.HashData(
            Encoding.UTF8.GetBytes($".{CytoscapeContainerClass}{CytoscapeRuleSuffix}")));

        using HttpResponseMessage document = await client.GetAsync("/graph");
        string policy = Policy(document);

        // Allowed by hash rather than by opening the directive, so this one rule is permitted and no
        // other inline stylesheet is. Should a Cytoscape upgrade change either the class or the
        // rule, the assertions above fail by name instead of the graph quietly losing its
        // positioning context while the library complains in a console nobody is watching.
        Assert.Contains($"style-src 'self' 'sha256-{expected}';", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-inline'", StyleDirective(policy), StringComparison.Ordinal);

        // Style attributes are their own directive because several components position or colour a
        // single element from data — a menu at the point that was right-clicked, a tag pill in its
        // own colour — and no stylesheet can say that. Allowing them is not allowing a <style>
        // block, nor a stylesheet from anywhere.
        Assert.Contains("style-src-attr 'unsafe-inline'", policy, StringComparison.Ordinal);
    }

    /// <summary>
    /// The opening tag of every script the document holds inline, meaning every one without a src of
    /// its own. Those are the only scripts a nonce has anything to say about: one served as a file is
    /// already covered by 'self'.
    /// </summary>
    private static List<string> InlineScriptTags(string html)
    {
        List<string> tags = [];
        int index = 0;
        while ((index = html.IndexOf("<script", index, StringComparison.Ordinal)) >= 0)
        {
            int end = html.IndexOf('>', index);
            Assert.True(end >= 0, "A script tag in the document is never closed.");
            string tag = html[index..(end + 1)];
            if (!tag.Contains(" src=", StringComparison.Ordinal))
            {
                tags.Add(tag);
            }

            index = end + 1;
        }

        return tags;
    }

    private static string Policy(HttpResponseMessage response) =>
        Assert.Single(response.Headers.GetValues("Content-Security-Policy"));

    private static string Nonce(string policy)
    {
        const string marker = "'nonce-";
        int start = policy.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The policy named no script nonce: {policy}");
        start += marker.Length;
        return policy[start..policy.IndexOf('\'', start)];
    }

    private static string ScriptDirective(string policy) => Directive(policy, "script-src ");

    private static string StyleDirective(string policy) => Directive(policy, "style-src ");

    /// <summary>
    /// One directive on its own, because asserting the absence of 'unsafe-inline' against the whole
    /// header would pass while style-src-attr legitimately contains it.
    /// </summary>
    private static string Directive(string policy, string name)
    {
        int start = policy.IndexOf(name, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The policy named no {name.Trim()}: {policy}");
        int end = policy.IndexOf(';', start);
        return end < 0 ? policy[start..] : policy[start..end];
    }

    private static async Task<HttpClient> SignedInAsync(MonkeysphereApplicationFactory factory)
    {
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });

        string login = await client.GetStringAsync("/login");
        int name = login.IndexOf("name=\"__RequestVerificationToken\"", StringComparison.Ordinal);
        Assert.True(name >= 0, "The sign-in page rendered no antiforgery token.");
        int value = login.IndexOf("value=\"", name, StringComparison.Ordinal) + "value=\"".Length;
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = login[value..login.IndexOf('"', value)],
            ["username"] = "admin",
            ["password"] = AdministratorPassword,
            ["returnUrl"] = "/",
        });

        using HttpResponseMessage signIn = await client.PostAsync("/auth/login", form);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        return client;
    }
}
