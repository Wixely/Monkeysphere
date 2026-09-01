using System.Net;
using System.Text.RegularExpressions;

namespace Monkeysphere.Web.Tests;

public sealed class SetupWizardTests
{
    [Fact]
    public async Task AuthenticatedNewDatasetShowsStarterExamples()
    {
        await using MonkeysphereApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        string loginHtml = await client.GetStringAsync("/login");
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(loginHtml),
            ["username"] = "admin",
            ["password"] = "test-only-LongPassword-2048!",
            ["returnUrl"] = "/setup",
        });
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/auth/login", form)).StatusCode);

        string setupHtml = await client.GetStringAsync("/setup");

        Assert.Contains("What would you like to remember?", setupHtml, StringComparison.Ordinal);
        Assert.Contains("Just people", setupHtml, StringComparison.Ordinal);
        Assert.Contains("People and everyday life", setupHtml, StringComparison.Ordinal);
        Assert.Contains("Your whole world", setupHtml, StringComparison.Ordinal);
        Assert.Contains("the family car", setupHtml, StringComparison.Ordinal);
        Assert.Contains("a favourite video game", setupHtml, StringComparison.Ordinal);
        Assert.Contains("a memorable trip", setupHtml, StringComparison.Ordinal);
        Assert.Equal(
            4,
            Regex.Count(
                setupHtml,
                "<button[^>]+class=\"setup-tier [^\"]*\"[^>]+aria-pressed=\"false\"",
                RegexOptions.CultureInvariant));
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        Match match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : throw new InvalidOperationException("Antiforgery token not found.");
    }
}
