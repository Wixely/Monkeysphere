using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// A record type created after somebody had arranged the dashboard never reached it. The stored
/// categories were an ordered list of what had been chosen, and a type that did not exist when that
/// list was written is named by it no more than one that had been deliberately removed — so the new
/// type was read as unwanted and stayed off the page until an administrator noticed and added it.
///
/// Removals are now recorded separately, which is what lets absence mean "new". These pin both
/// readings, because a fix that only made new types appear would also undo every removal.
/// </summary>
public sealed class DashboardCategoryVisibilityTests
{
    [Fact]
    public async Task ATypeCreatedAfterTheDashboardWasArrangedAppearsOnIt()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IDashboardService dashboard = application.Services.GetRequiredService<IDashboardService>();

        RecordType people = await records.CreateRecordTypeAsync("People");
        RecordType places = await records.CreateRecordTypeAsync("Places");

        // Arranged deliberately: both categories, places first.
        DashboardConfiguration arranged = await dashboard.SaveConfigurationAsync(new([places.Id, people.Id], []));
        Assert.Equal([places.Id, people.Id], arranged.RecordTypeIds);

        RecordType projects = await records.CreateRecordTypeAsync("Projects");

        // The reported case. The arrangement is untouched and keeps its order; the new type follows it
        // rather than being read as one somebody had removed.
        DashboardConfiguration resolved = await dashboard.GetConfigurationAsync();
        Assert.Equal([places.Id, people.Id, projects.Id], resolved.RecordTypeIds);
    }

    [Fact]
    public async Task RemovingACategoryKeepsItOffTheDashboard()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IDashboardService dashboard = application.Services.GetRequiredService<IDashboardService>();

        RecordType people = await records.CreateRecordTypeAsync("People");
        RecordType places = await records.CreateRecordTypeAsync("Places");

        // Places is deliberately left out, which has to mean something different from a type that did
        // not exist yet — otherwise the fix above would simply ignore every removal.
        _ = await dashboard.SaveConfigurationAsync(new([people.Id], []));
        Assert.Equal([people.Id], (await dashboard.GetConfigurationAsync()).RecordTypeIds);

        // Still off after an unrelated save, so the removal is stored rather than merely absent from
        // one response.
        _ = await dashboard.SaveConfigurationAsync(new([people.Id], [], 30));
        DashboardConfiguration reloaded = await dashboard.GetConfigurationAsync();
        Assert.Equal([people.Id], reloaded.RecordTypeIds);
        Assert.Equal(30, reloaded.UpcomingDays);

        // Putting it back works, and does not leave a stale removal behind that would take it away
        // again on the next read.
        _ = await dashboard.SaveConfigurationAsync(new([people.Id, places.Id], [], 30));
        Assert.Equal([people.Id, places.Id], (await dashboard.GetConfigurationAsync()).RecordTypeIds);

        // Removing everything is a legitimate choice and survives: an empty dashboard is not an
        // unconfigured one.
        _ = await dashboard.SaveConfigurationAsync(new([], [], 30));
        Assert.Empty((await dashboard.GetConfigurationAsync()).RecordTypeIds);
    }

    [Fact]
    public async Task ADeploymentThatNeverConfiguredItsDashboardSeesWhatItHas()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        await application.Services.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IDashboardService dashboard = application.Services.GetRequiredService<IDashboardService>();
        RecordType person = (await records.ListRecordTypesAsync()).Single(type => type.PresetKey == "monkeysphere.person");
        RecordType projects = await records.CreateRecordTypeAsync("Projects");

        DashboardConfiguration derived = await dashboard.GetConfigurationAsync();

        // People first, because that is what a records application is usually about, then everything
        // else. This used to be people *only*, which meant an operator who never opened the settings
        // page never saw anything they created afterwards either.
        Assert.Equal(person.Id, derived.RecordTypeIds[0]);
        Assert.Contains(projects.Id, derived.RecordTypeIds);

        // The birthday field is still picked up, so the upcoming-dates panel works out of the box.
        Assert.NotEmpty(derived.RecurringFieldDefinitionIds);
        Assert.Equal(DashboardService.DefaultUpcomingDays, derived.UpcomingDays);
    }

    [Fact]
    public async Task TheDashboardIsBoundedEvenWhenTypesArriveOnTheirOwn()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IDashboardService dashboard = application.Services.GetRequiredService<IDashboardService>();

        List<Guid> created = [];
        for (int index = 0; index < DashboardService.MaximumCategories + 4; index++)
        {
            created.Add((await records.CreateRecordTypeAsync($"Type {index:00}")).Id);
        }

        // Each category costs a search and a read per row, so the page's work would otherwise grow
        // with the deployment's type list rather than with anybody's choice.
        Assert.Equal(DashboardService.MaximumCategories, (await dashboard.GetConfigurationAsync()).RecordTypeIds.Count);

        // A chosen category keeps its place: the bound spends its places on what was asked for before
        // it spends them on what arrived by itself.
        Guid last = created[^1];
        _ = await dashboard.SaveConfigurationAsync(new([last], []));
        DashboardConfiguration resolved = await dashboard.GetConfigurationAsync();
        Assert.Equal(last, resolved.RecordTypeIds[0]);
        Assert.Single(resolved.RecordTypeIds);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            dashboard.SaveConfigurationAsync(new(created, [])));
    }

    [Fact]
    public async Task ARetiredTypeLeavesNothingBehindOnTheDashboard()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IDashboardService dashboard = application.Services.GetRequiredService<IDashboardService>();

        RecordType people = await records.CreateRecordTypeAsync("People");
        RecordType places = await records.CreateRecordTypeAsync("Places");
        _ = await dashboard.SaveConfigurationAsync(new([people.Id, places.Id], []));

        RecordTypeRetirementPreview preview = await records.PreviewRecordTypeRetirementAsync(places.Id);
        await records.RetireRecordTypeAsync(places.Id, preview.Revision);

        // Gone because it is retired, not because it was removed, and the arrangement it was part of
        // still resolves rather than carrying a name nothing can draw.
        Assert.Equal([people.Id], (await dashboard.GetConfigurationAsync()).RecordTypeIds);
    }
}
