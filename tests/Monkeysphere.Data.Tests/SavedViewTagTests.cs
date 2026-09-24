using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// Tags were made universal, and an ad-hoc search could already narrow by them, but a saved view
/// could not carry that narrowing: everything it stored was keyed to a field definition, and a
/// universal tag has none. These pin the two halves that closes — a view remembers the tags it
/// requires, and turning it into a search actually applies them.
/// </summary>
public sealed class SavedViewTagTests
{
    [Fact]
    public async Task ASavedViewRemembersTheTagsItRequiresAndWhetherToShowThem()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        ISavedViewService views = application.Services.GetRequiredService<ISavedViewService>();

        RecordType type = await records.CreateRecordTypeAsync("Tagged person");

        SavedViewDetails created = await views.CreateAsync(new(
            "Working Londoners", type.Id, null, [], [], Tags: ["  Work  ", "london", "WORK"], ShowTags: true));

        // Normalized by the same rules a record's own tags follow, so a view cannot be saved asking
        // for a tag no record could carry, and the repeat collapses rather than being stored twice.
        Assert.Equal(["Work", "london"], created.Tags);
        Assert.True(created.View.ShowTags);

        // Read back through a fresh scope, because a view that only remembered this in the instance
        // that saved it would look right here and be gone on the next request.
        SavedViewDetails reloaded = (await application.Services
            .GetRequiredService<ISavedViewService>().GetAsync(created.View.Id))!;
        Assert.Equal(["Work", "london"], reloaded.Tags);
        Assert.True(reloaded.View.ShowTags);

        // Editing replaces the list rather than adding to it.
        SavedViewDetails updated = await views.UpdateAsync(created.View.Id, new(
            "Working Londoners", type.Id, null, [], [], Tags: ["navy"], ShowTags: false));
        Assert.Equal(["navy"], updated.Tags);
        Assert.False(updated.View.ShowTags);

        // A copy is the same view under another name, which has to include what it selects on.
        SavedViewDetails copy = await views.DuplicateAsync(created.View.Id, "Sailors");
        Assert.Equal(["navy"], copy.Tags);

        await Assert.ThrowsAsync<DomainValidationException>(() => views.CreateAsync(new(
            "Too many", type.Id, null, [], [],
            Tags: [.. Enumerable.Range(0, SavedViewService.MaximumTags + 1).Select(index => $"tag{index}")])));
    }

    [Fact]
    public async Task TheViewSelectsOnlyRecordsCarryingEveryTagItLists()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        ISavedViewService views = application.Services.GetRequiredService<ISavedViewService>();

        RecordType type = await records.CreateRecordTypeAsync("Selected person");
        _ = await records.CreateRecordAsync(type.Id, "Both", [], null, ["work", "london"]);
        _ = await records.CreateRecordAsync(type.Id, "Work only", [], null, ["work"]);
        _ = await records.CreateRecordAsync(type.Id, "Neither", []);

        SavedViewDetails view = await views.CreateAsync(new(
            "Working Londoners", type.Id, null, [], [], Tags: ["work", "london"]));

        // Each tag narrows: a record carrying only one of them is not in the view. This is the
        // meaning an ad-hoc search already gave the same list, and the two must not disagree.
        PagedResult<RecordSummary> selected = await records.SearchRecordsAsync(views.ToSearch(view));
        Assert.Equal("Both", Assert.Single(selected.Items).DisplayName);

        // A view listing no tags is not a view selecting nothing.
        SavedViewDetails everyone = await views.CreateAsync(new("Everyone", type.Id, null, [], []));
        Assert.Equal(3, (await records.SearchRecordsAsync(views.ToSearch(everyone))).TotalCount);

        // Case is not part of what a tag is, here or in storage.
        SavedViewDetails shouted = await views.CreateAsync(new(
            "Shouted", type.Id, null, [], [], Tags: ["WORK"]));
        Assert.Equal(2, (await records.SearchRecordsAsync(views.ToSearch(shouted))).TotalCount);
    }
}
