using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// Once a record type started reaching the dashboard without being asked for, a fresh deployment's
/// front page became mostly panels reading "No workplace yet" — three of them above anything useful,
/// because an empty category was drawn exactly as large as a full one. That was only visible by
/// looking at the page, which is how it was found.
///
/// An empty category is now one button in a shared row. These pin both halves, because dropping the
/// panel and dropping the category are easy to confuse and only one of them is wanted: somebody has
/// to be able to add the first record of a type they have just created.
/// </summary>
public sealed class DashboardEmptyCategoryTests
{
    private const string AdministratorPassword = "test-only-LongPassword-2048!";

    [Fact]
    public async Task AnEmptyCategoryKeepsItsAddLinkWithoutTakingAPanel()
    {
        await using MonkeysphereApplicationFactory factory = new();
        RecordType people;
        RecordType workplaces;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            people = await records.CreateRecordTypeAsync("Person");
            workplaces = await records.CreateRecordTypeAsync("Workplace");
            _ = await records.CreateRecordAsync(people.Id, "Ada Lovelace", []);
        }

        string html = await SignedInDashboardAsync(factory);

        // The populated one still gets its panel, with its records in it.
        Assert.Contains($"dashboard-records-{people.Id}", html, StringComparison.Ordinal);
        Assert.Contains("Ada Lovelace", html, StringComparison.Ordinal);

        // The empty one does not, and the placeholder that used to fill it is gone.
        Assert.DoesNotContain($"dashboard-records-{workplaces.Id}", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No workplace yet", html, StringComparison.OrdinalIgnoreCase);

        // But it is still reachable, which is the point of keeping it at all: a type somebody just
        // created is where they add its first record.
        Assert.Contains("Nothing yet", html, StringComparison.Ordinal);
        Assert.Contains($"/records/new?typeId={workplaces.Id}", html, StringComparison.Ordinal);
        Assert.Contains("Add Workplace", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADashboardWithNothingOnItAtAllSaysSoRatherThanShowingAnEmptyRow()
    {
        await using MonkeysphereApplicationFactory factory = new();
        RecordType workplaces;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            workplaces = await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>()
                .CreateRecordTypeAsync("Workplace");
        }

        string html = await SignedInDashboardAsync(factory);

        // Every category is empty here, so the row carries all of them and no records panel appears.
        Assert.Contains("Nothing yet", html, StringComparison.Ordinal);
        Assert.Contains($"/records/new?typeId={workplaces.Id}", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"dashboard-records-{workplaces.Id}", html, StringComparison.Ordinal);
    }

    private static async Task<string> SignedInDashboardAsync(MonkeysphereApplicationFactory factory)
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

        using HttpResponseMessage signIn = await client.PostAsync("/auth/login", form);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        return await client.GetStringAsync("/");
    }
}
