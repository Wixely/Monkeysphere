using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// Merging two date fields has to collapse the reminders that follow their values, and which reminder
/// survives has to be the one whose value survives.
///
/// This became load-bearing when dismissal stopped being permanent. The de-duplication had been written
/// when a dismissed reminder was finished, so it only collapsed a pair with neither dismissed and left
/// the rest alone as harmless. Once a dismissed reminder comes back for its next occurrence, "the rest"
/// are two live reminders on one value and one lead time, which the uniqueness rule no longer permits:
/// the merge failed on the index instead of finishing.
/// </summary>
public sealed class FieldMergeReminderTests
{
    [Fact]
    public async Task MergingFieldsCollapsesRemindersEvenWhenOneOfThePairWasDismissed()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IReminderService reminders = application.Services.GetRequiredService<IReminderService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        FieldDefinition source = await records.CreateAndAttachFieldAsync(person.Id, new("Birthday", FieldTypes.ExactDate, false));
        FieldDefinition target = await records.CreateAndAttachFieldAsync(person.Id, new("Date of birth", FieldTypes.ExactDate, false));

        // One record carrying the same day under both names and a reminder on each, at the same lead:
        // after the merge these are one value with two reminders that cannot both exist.
        RecordDetails ada = await records.CreateRecordAsync(person.Id, "Ada",
            [new(source.Id, "1815-12-10"), new(target.Id, "1815-12-10")]);
        Guid sourceReminder = (await reminders.CreateAsync(
            ada.Values.Single(value => value.FieldDefinitionId == source.Id).Id, 7)).Id;
        Guid targetReminder = (await reminders.CreateAsync(
            ada.Values.Single(value => value.FieldDefinitionId == target.Id).Id, 7)).Id;

        // Dismissed, which used to mean gone for good and now means only "dealt with that occurrence".
        Assert.True(await reminders.DismissAsync(sourceReminder));

        FieldMergePreview preview = await records.PreviewFieldMergeAsync(source.Id, target.Id);
        Assert.True(preview.IsCompatible);
        Assert.Equal(1, preview.ConflictingValueCount);

        await records.MergeFieldsAsync(source.Id, target.Id, FieldMergeConflictResolution.KeepTarget, preview.Revision);

        // The target's value won, so the target's reminder is the one still standing: a reminder names a
        // value, and it follows the value the conflict policy kept rather than the dismissal state of
        // either row.
        StoredReminder surviving = Assert.Single(await ListStoredAsync(application));
        Assert.Equal(targetReminder, surviving.Reminder.Id);
        Assert.Equal(target.Id, surviving.Reminder.FieldDefinitionId);
        Assert.Null(surviving.DismissedForDate);

        // And it is still armed, so the merge did not silently inherit the dismissal of the row it
        // discarded.
        ReminderItem active = Assert.Single(await reminders.ListActiveAsync());
        Assert.Equal(targetReminder, active.Reminder.Id);
    }

    [Fact]
    public async Task KeepingTheSourceValueKeepsTheSourceReminder()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IReminderService reminders = application.Services.GetRequiredService<IReminderService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        FieldDefinition source = await records.CreateAndAttachFieldAsync(person.Id, new("Birthday", FieldTypes.ExactDate, false));
        FieldDefinition target = await records.CreateAndAttachFieldAsync(person.Id, new("Date of birth", FieldTypes.ExactDate, false));
        RecordDetails ada = await records.CreateRecordAsync(person.Id, "Ada",
            [new(source.Id, "1815-12-10"), new(target.Id, "1815-12-10")]);
        Guid sourceReminder = (await reminders.CreateAsync(
            ada.Values.Single(value => value.FieldDefinitionId == source.Id).Id, 7)).Id;
        Guid targetReminder = (await reminders.CreateAsync(
            ada.Values.Single(value => value.FieldDefinitionId == target.Id).Id, 7)).Id;

        // Dismissed on the side the policy is about to keep, which is the mirror of the first case: the
        // surviving reminder is still the one whose value survived.
        Assert.True(await reminders.DismissAsync(sourceReminder));

        FieldMergePreview preview = await records.PreviewFieldMergeAsync(source.Id, target.Id);
        await records.MergeFieldsAsync(source.Id, target.Id, FieldMergeConflictResolution.KeepSource, preview.Revision);

        StoredReminder surviving = Assert.Single(await ListStoredAsync(application));
        Assert.Equal(sourceReminder, surviving.Reminder.Id);
        Assert.NotEqual(targetReminder, surviving.Reminder.Id);
        Assert.Equal(target.Id, surviving.Reminder.FieldDefinitionId);

        // It kept its own dismissal, because it is the same reminder on the same value: nothing about a
        // merge is a reason to re-arm something somebody has already dealt with.
        Assert.Equal(new DateOnly(1815, 12, 10), surviving.DismissedForDate);
        Assert.Empty(await reminders.ListActiveAsync());
    }

    private static async Task<IReadOnlyList<StoredReminder>> ListStoredAsync(TestApplication application) =>
        await application.Services.GetRequiredService<IReminderStore>().ListAsync();
}
