using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// The graph's fullscreen element is a wrapper around the canvas rather than the canvas itself,
/// and the reason is an accessibility one: the record-centring combobox is the graph's only
/// non-visual alternative, and it is a sibling of the canvas rather than a child. Fullscreening
/// the canvas alone would take that alternative off the screen at exactly the moment the graph
/// fills it, and the controls that act on the graph would go with it. These pin the containment,
/// because it is the kind of thing a later markup tidy-up would quietly undo.
/// </summary>
public sealed class GraphFullscreenTests
{
    private const string AdministratorPassword = "test-only-LongPassword-2048!";

    [Fact]
    public async Task TheFullscreenStageContainsTheGraphsAccessibleAlternativeAndControls()
    {
        await using MonkeysphereApplicationFactory factory = new();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordType type = await records.CreateRecordTypeAsync("Fullscreen type");
            _ = await records.CreateRecordAsync(type.Id, "Fullscreen record", []);
        }

        string html = await SignedInGraphPageAsync(factory);
        string stage = Stage(html);

        // The combobox lists every displayed node and drives the same selection the canvas does.
        Assert.Contains("Centre a displayed record", stage, StringComparison.Ordinal);

        // A control the operator cannot reach in fullscreen is a control they have to leave
        // fullscreen to use, so these live with the canvas rather than in the page heading.
        Assert.Contains("Reset viewport", stage, StringComparison.Ordinal);

        // The canvas itself, and the description its aria-describedby points at.
        Assert.Contains("relationship-graph-description", stage, StringComparison.Ordinal);

        // Were the extraction above ever to return more than the stage, every assertion here would
        // pass whatever the markup did. This is the filter panel, deliberately outside the stage.
        Assert.DoesNotContain("Record name or alias", stage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingIsReachableFromInsideTheFullscreenStage()
    {
        await using MonkeysphereApplicationFactory factory = new();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordType type = await records.CreateRecordTypeAsync("Arranged type");
            _ = await records.CreateRecordAsync(type.Id, "Arranged record", []);
        }

        string stage = Stage(await SignedInGraphPageAsync(factory));

        // A layout arranged in fullscreen had to be left before it could be kept, because the only
        // save control was in the panel below the graph and fullscreen does not show it.
        Assert.Contains("graph-save", stage, StringComparison.Ordinal);

        // The panel below is still where a view is named and first created, and stays outside.
        Assert.DoesNotContain("Reusable graph filter", stage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBoundaryTheGraphStoppedDrawingAtIsSaidInsideTheFullscreenStage()
    {
        await using MonkeysphereApplicationFactory factory = new();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IGraphSettingsService settings = scope.ServiceProvider.GetRequiredService<IGraphSettingsService>();
            _ = await settings.SaveAsync(new(NodeLimit: RelationshipGraphService.MinimumNodes));

            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordType type = await records.CreateRecordTypeAsync("Crowded type");
            for (int index = 0; index < RelationshipGraphService.MinimumNodes + 2; index++)
            {
                _ = await records.CreateRecordAsync(type.Id, $"Record {index}", []);
            }
        }

        string stage = Stage(await SignedInGraphPageAsync(factory));

        // Said where the graph is, not above the panel. Outside the stage it was invisible in
        // fullscreen, which is exactly where an operator is most likely to believe the graph is
        // showing them everything that matches.
        Assert.Contains("so some of what matches is not drawn", stage, StringComparison.Ordinal);

        // And the way out of it goes with the notice, because raising the limit is the one remedy
        // that does not require leaving fullscreen first.
        Assert.Contains("/settings/graph", stage, StringComparison.Ordinal);
    }

    private static async Task<string> SignedInGraphPageAsync(MonkeysphereApplicationFactory factory)
    {
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
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

        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/auth/login", form)).StatusCode);
        return await client.GetStringAsync("/graph");
    }

    /// <summary>
    /// The stage's own markup, found by matching its opening tag to its close. Searching the whole
    /// page would pass whether or not the elements are actually inside the element that goes
    /// fullscreen, which is the only thing worth asserting here.
    /// </summary>
    private static string Stage(string html)
    {
        const string opening = "<div class=\"graph-stage\"";
        int start = html.IndexOf(opening, StringComparison.Ordinal);
        Assert.True(start >= 0, "The graph page rendered no fullscreen stage.");

        int depth = 0;
        int index = start;
        while (true)
        {
            int open = html.IndexOf("<div", index, StringComparison.Ordinal);
            int close = html.IndexOf("</div>", index, StringComparison.Ordinal);
            Assert.True(close >= 0, "The fullscreen stage is never closed.");
            if (open >= 0 && open < close)
            {
                depth++;
                index = open + 4;
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return html[start..(close + "</div>".Length)];
            }

            index = close + "</div>".Length;
        }
    }
}
