using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// Merging two records that turned out to be the same person.
///
/// The promise the whole feature rests on is that nothing the merged-away record held is lost. What the
/// survivor's type can hold becomes its data; everything else — including every value somebody chose
/// not to keep — becomes retained source material, in the same place and readable on the same screen as
/// the lines of an imported vCard the application never understood. These cover that promise, and the
/// several constraints a merge has to step around to keep it.
/// </summary>
public sealed class RecordMergeTests
{
    [Fact]
    public async Task MergingTwoDuplicatePeopleCarriesWhatItCanAndArchivesEverythingElse()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        FieldDefinition birthday = await records.CreateAndAttachFieldAsync(person.Id, new("Date of birth", FieldTypes.ExactDate, false));
        FieldDefinition phone = await records.CreateAndAttachFieldAsync(person.Id, new("Phone", FieldTypes.Text, false));

        // The situation an import leaves behind: the same person twice, each half filled in, and the two
        // halves disagreeing about the one thing both happen to know.
        RecordDetails keep = await records.CreateRecordAsync(person.Id, "Ada Lovelace",
            [new(birthday.Id, "1815-12-10")], ["Ada Byron"], ["mathematicians"]);
        RecordDetails lose = await records.CreateRecordAsync(person.Id, "Augusta Ada King",
            [new(birthday.Id, "1815-12-11"), new(phone.Id, "555-0100")], tags: ["peers"]);

        RecordMergePreview preview = Assert.IsType<RecordMergePreview>(
            await records.PreviewRecordMergeAsync(keep.Record.Id, lose.Record.Id, null));
        Assert.Null(preview.Refusal);

        // Only the field both records hold a value for is a decision. The phone number is not: the
        // survivor has no opinion about it, so asking would be asking nothing.
        RecordMergeValueConflict conflict = Assert.Single(preview.Conflicts);
        Assert.Equal(birthday.Id, conflict.FieldDefinitionId);
        Assert.Equal(RecordMergeResolution.KeepSurviving, conflict.Resolution);
        Assert.Equal(1, preview.Impact.FieldValuesCarried);
        Assert.Equal(1, preview.Impact.FieldValuesArchivedOnly);

        // The other record's name is not a disagreement about what this person is called; it is another
        // name they went by, which is what aliases are for.
        Assert.Equal(["Augusta Ada King"], preview.AliasesAdded);
        Assert.Equal(["peers"], preview.TagsAdded);

        await records.MergeRecordsAsync(keep.Record.Id, lose.Record.Id, [], preview.Revision);

        Assert.Null(await records.GetRecordAsync(lose.Record.Id));
        RecordDetails merged = Assert.IsType<RecordDetails>(await records.GetRecordAsync(keep.Record.Id));
        Assert.Equal("Ada Lovelace", merged.Record.DisplayName);
        Assert.Equal(["Ada Byron", "Augusta Ada King"], merged.Aliases);
        Assert.Equal(["mathematicians", "peers"], merged.Tags.Order(StringComparer.Ordinal));
        Assert.Equal("1815-12-10", Assert.Single(merged.Values, value => value.FieldDefinitionId == birthday.Id).DateValue);
        Assert.Equal("555-0100", Assert.Single(merged.Values, value => value.FieldDefinitionId == phone.Id).TextValue);

        // And the promise: the day that lost is still readable, on the record that survived, in the same
        // place an unrecognised vCard line would be.
        RecordSourceSnapshot sources = Assert.IsType<RecordSourceSnapshot>(
            await application.Services.GetRequiredService<IRecordSourceService>().GetAsync(keep.Record.Id));
        RecordSourceImport archive = Assert.Single(sources.Imports);
        Assert.Equal(RecordSourceKinds.Merge, archive.SourceKind);
        Assert.Contains("Augusta Ada King", archive.SourceFormat);
        Assert.Contains("1815-12-11", sources.Values.Select(value => value.ValuePreview));
        Assert.Contains("Augusta Ada King", sources.Values.Select(value => value.ValuePreview));

        // Including the values that were carried, so the archive reads as what the deleted record was
        // rather than as a list of what happened to be discarded.
        Assert.Contains("555-0100", sources.Values.Select(value => value.ValuePreview));
    }

    [Fact]
    public async Task TakingTheOtherRecordsValueArchivesTheSurvivorsOwn()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        FieldDefinition birthday = await records.CreateAndAttachFieldAsync(person.Id, new("Date of birth", FieldTypes.ExactDate, false));
        RecordDetails keep = await records.CreateRecordAsync(person.Id, "Ada", [new(birthday.Id, "1815-12-11")]);
        RecordDetails lose = await records.CreateRecordAsync(person.Id, "Ada L", [new(birthday.Id, "1815-12-10")]);

        RecordMergePreview preview = Assert.IsType<RecordMergePreview>(await records.PreviewRecordMergeAsync(
            keep.Record.Id, lose.Record.Id, [new(birthday.Id, RecordMergeResolution.TakeMerged)]));

        // The preview has to report the survivor's own value as the one being archived, because that is
        // the half a person is agreeing to give up and it is the opposite of the default.
        RecordMergeUncarriedValue archived = Assert.Single(preview.Archived);
        Assert.Equal(RecordMergeUncarriedReasons.SurvivingValueReplaced, archived.Reason);
        Assert.Equal("1815-12-11", Assert.Single(archived.Values).DateValue);

        await records.MergeRecordsAsync(keep.Record.Id, lose.Record.Id,
            [new(birthday.Id, RecordMergeResolution.TakeMerged)], preview.Revision);

        RecordDetails merged = Assert.IsType<RecordDetails>(await records.GetRecordAsync(keep.Record.Id));
        RecordValue value = Assert.Single(merged.Values);
        Assert.Equal("1815-12-10", value.DateValue);

        // Replacing the survivor's values frees their ordinals, so the incoming one starts at the bottom
        // rather than being appended after a value that is no longer there.
        Assert.Equal(0, value.Ordinal);

        // And the day this record used to hold is still readable. Choosing the other record's value is
        // the one path where the survivor's own data is what gets displaced, so it is the one path where
        // an archive that only recorded the deleted record would quietly lose something.
        RecordSourceSnapshot sources = Assert.IsType<RecordSourceSnapshot>(
            await application.Services.GetRequiredService<IRecordSourceService>().GetAsync(keep.Record.Id));
        RecordSourceValue replaced = Assert.Single(sources.Values, entry => entry.Name.EndsWith("(replaced)", StringComparison.Ordinal));
        Assert.Equal("1815-12-11", replaced.ValuePreview);
        Assert.Equal(RecordSourceMapping.Opaque, replaced.Mapping);
    }

    [Fact]
    public async Task KeepingBothPutsTwoValuesOnAPlainFieldAndOneCombinedListOnATagsField()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        FieldDefinition phone = await records.CreateAndAttachFieldAsync(person.Id, new("Phone", FieldTypes.Text, false));
        FieldDefinition skills = await records.CreateAndAttachFieldAsync(person.Id, new("Skills", FieldTypes.Tags, false));
        RecordDetails keep = await records.CreateRecordAsync(person.Id, "Ada",
            [new(phone.Id, "555-0100"), new(skills.Id, Tags: ["maths", "notation"])]);
        RecordDetails lose = await records.CreateRecordAsync(person.Id, "Ada L",
            [new(phone.Id, "555-0200"), new(skills.Id, Tags: ["notation", "engines"])]);

        RecordMergeChoice[] choices =
        [
            new(phone.Id, RecordMergeResolution.KeepBoth),
            new(skills.Id, RecordMergeResolution.KeepBoth),
        ];
        RecordMergePreview preview = Assert.IsType<RecordMergePreview>(
            await records.PreviewRecordMergeAsync(keep.Record.Id, lose.Record.Id, choices));
        await records.MergeRecordsAsync(keep.Record.Id, lose.Record.Id, choices, preview.Revision);

        RecordDetails merged = Assert.IsType<RecordDetails>(await records.GetRecordAsync(keep.Record.Id));

        // Two phone numbers are a better answer than throwing one away, and nothing in the model says a
        // field holds only one, so keeping both means exactly that.
        Assert.Equal(["555-0100", "555-0200"],
            merged.Values.Where(value => value.FieldDefinitionId == phone.Id)
                .OrderBy(value => value.Ordinal).Select(value => value.TextValue));

        // But two lists of tags on one field would read as two sets of skills rather than one set, so
        // keeping both here combines them, and a skill both records named is named once.
        RecordValue combined = Assert.Single(merged.Values, value => value.FieldDefinitionId == skills.Id);
        Assert.Equal(["maths", "notation", "engines"], combined.Tags);
    }

    [Fact]
    public async Task ARelationshipFollowsItsRecordUnlessDoingSoWouldInventOrDuplicateOne()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        RelationshipType parentOf = await relationships.CreateTypeAsync(new(
            "parent of", RelationshipDirectionality.Directional, "child of"));

        RecordDetails keep = await records.CreateRecordAsync(person.Id, "Ada", []);
        RecordDetails lose = await records.CreateRecordAsync(person.Id, "Ada L", []);
        RecordDetails child = await records.CreateRecordAsync(person.Id, "Byron", []);
        RecordDetails mother = await records.CreateRecordAsync(person.Id, "Annabella", []);
        RecordDetails friend = await records.CreateRecordAsync(person.Id, "Charles", []);

        // Both duplicates recorded the same child, so one of the two has to go.
        await relationships.CreateAsync(parentOf.Id, keep.Record.Id, child.Record.Id);
        await relationships.CreateAsync(parentOf.Id, lose.Record.Id, child.Record.Id);

        // The duplicates were also related to each other, which is how somebody usually notices they are
        // duplicates. Repointing that would make the survivor its own parent.
        await relationships.CreateAsync(parentOf.Id, lose.Record.Id, keep.Record.Id);

        // The survivor is someone's child and the loser is someone's parent. Under a directional type
        // those are not the same relationship, so neither may be mistaken for a duplicate of the other.
        await relationships.CreateAsync(parentOf.Id, mother.Record.Id, keep.Record.Id);
        await relationships.CreateAsync(parentOf.Id, lose.Record.Id, friend.Record.Id);

        RecordMergePreview preview = Assert.IsType<RecordMergePreview>(
            await records.PreviewRecordMergeAsync(keep.Record.Id, lose.Record.Id, null));
        Assert.Equal(2, preview.Impact.RelationshipsDropped);
        Assert.Equal(1, preview.Impact.RelationshipsRepointed);

        await records.MergeRecordsAsync(keep.Record.Id, lose.Record.Id, [], preview.Revision);

        IReadOnlyList<RelationshipView> after = await relationships.ListForRecordAsync(keep.Record.Id);
        Assert.Equal(3, after.Count);
        Assert.Single(after, view => view.RelatedRecordId == child.Record.Id && view.IsOutgoing);
        Assert.Single(after, view => view.RelatedRecordId == mother.Record.Id && !view.IsOutgoing);
        Assert.Single(after, view => view.RelatedRecordId == friend.Record.Id && view.IsOutgoing);
    }

    [Fact]
    public async Task ASymmetricRelationshipIsStoredTheWayRoundTheRestOfTheApplicationExpects()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        RelationshipType sibling = await relationships.CreateTypeAsync(new("sibling of", RelationshipDirectionality.Symmetric));

        RecordDetails keep = await records.CreateRecordAsync(person.Id, "Ada", []);
        RecordDetails lose = await records.CreateRecordAsync(person.Id, "Ada L", []);
        RecordDetails other = await records.CreateRecordAsync(person.Id, "Medora", []);
        await relationships.CreateAsync(sibling.Id, lose.Record.Id, other.Record.Id);

        RecordMergePreview preview = Assert.IsType<RecordMergePreview>(
            await records.PreviewRecordMergeAsync(keep.Record.Id, lose.Record.Id, null));
        await records.MergeRecordsAsync(keep.Record.Id, lose.Record.Id, [], preview.Revision);

        // A symmetric type keeps its ends in a fixed order so one pair cannot also be stored the other
        // way round. Repointing can put them out of that order, and the proof that it did not is that
        // creating the same relationship again is still refused as already existing — in either
        // direction, which is the whole point of the canonical order.
        Assert.Single(await relationships.ListForRecordAsync(keep.Record.Id));
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            relationships.CreateAsync(sibling.Id, keep.Record.Id, other.Record.Id));
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            relationships.CreateAsync(sibling.Id, other.Record.Id, keep.Record.Id));
    }

    [Fact]
    public async Task AReminderIsRenumberedOntoTheOrdinalItsValueLandsOn()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IReminderService reminders = application.Services.GetRequiredService<IReminderService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        FieldDefinition dates = await records.CreateAndAttachFieldAsync(person.Id, new("Anniversary", FieldTypes.ExactDate, false));

        RecordDetails keep = await records.CreateRecordAsync(person.Id, "Ada", [new(dates.Id, "1835-07-08")]);
        RecordDetails lose = await records.CreateRecordAsync(person.Id, "Ada L", [new(dates.Id, "1852-11-27")]);
        Guid survivingReminder = (await reminders.CreateAsync(Assert.Single(keep.Values).Id, 7)).Id;
        Guid carriedReminder = (await reminders.CreateAsync(Assert.Single(lose.Values).Id, 7)).Id;

        RecordMergeChoice[] choices = [new(dates.Id, RecordMergeResolution.KeepBoth)];
        RecordMergePreview preview = Assert.IsType<RecordMergePreview>(
            await records.PreviewRecordMergeAsync(keep.Record.Id, lose.Record.Id, choices));
        // Counted as what the merge moves, so this is the other record's reminder. The survivor's own is
        // not "carried" by anything: it stays where it already is.
        Assert.Equal(1, preview.Impact.RemindersCarried);
        Assert.Equal(0, preview.Impact.RemindersDropped);

        await records.MergeRecordsAsync(keep.Record.Id, lose.Record.Id, choices, preview.Revision);

        RecordDetails merged = Assert.IsType<RecordDetails>(await records.GetRecordAsync(keep.Record.Id));
        RecordValue moved = Assert.Single(merged.Values, value => value.DateValue == "1852-11-27");
        Assert.Equal(1, moved.Ordinal);

        // A reminder names its value by ordinal rather than by identity, so the carried value's reminder
        // has to be renumbered with it. Left alone it would still say ordinal 0 and so would quietly
        // become a reminder about the survivor's own date instead.
        IReadOnlyList<StoredReminder> stored = await application.Services.GetRequiredService<IReminderStore>().ListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal(0, Assert.Single(stored, entry => entry.Reminder.Id == survivingReminder).Reminder.ValueOrdinal);
        StoredReminder carried = Assert.Single(stored, entry => entry.Reminder.Id == carriedReminder);
        Assert.Equal(1, carried.Reminder.ValueOrdinal);
        Assert.Equal(keep.Record.Id, carried.Reminder.RecordId);
        Assert.Equal(new DateOnly(1852, 11, 27), carried.Entry.Date);
    }

    [Fact]
    public async Task AReminderOnAValueNobodyKeptGoesRatherThanPointingAtNothing()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IReminderService reminders = application.Services.GetRequiredService<IReminderService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        FieldDefinition birthday = await records.CreateAndAttachFieldAsync(person.Id, new("Date of birth", FieldTypes.ExactDate, false));

        RecordDetails keep = await records.CreateRecordAsync(person.Id, "Ada", [new(birthday.Id, "1815-12-10")]);
        RecordDetails lose = await records.CreateRecordAsync(person.Id, "Ada L", [new(birthday.Id, "1815-12-11")]);
        Guid survivingReminder = (await reminders.CreateAsync(Assert.Single(keep.Values).Id, 7)).Id;
        await reminders.CreateAsync(Assert.Single(lose.Values).Id, 7);

        // The default keeps the survivor's day, so the other record's reminder is about a date that is
        // about to stop being anybody's data. Carrying it would also collide with the survivor's own,
        // which is unique on exactly this field, ordinal and lead time.
        RecordMergePreview preview = Assert.IsType<RecordMergePreview>(
            await records.PreviewRecordMergeAsync(keep.Record.Id, lose.Record.Id, null));
        Assert.Equal(1, preview.Impact.RemindersDropped);

        await records.MergeRecordsAsync(keep.Record.Id, lose.Record.Id, [], preview.Revision);

        StoredReminder remaining = Assert.Single(await application.Services.GetRequiredService<IReminderStore>().ListAsync());
        Assert.Equal(survivingReminder, remaining.Reminder.Id);
        Assert.Equal(new DateOnly(1815, 12, 10), remaining.Entry.Date);
    }

    [Fact]
    public async Task MergingAcrossTypesKeepsTheSurvivorsTypeAndArchivesWhatItCannotHold()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        RecordType company = await records.CreateRecordTypeAsync("Company");
        FieldDefinition shared = await records.CreateAndAttachFieldAsync(person.Id, new("Phone", FieldTypes.Text, false));
        await records.AttachFieldAsync(company.Id, shared.Id, false);
        FieldDefinition companyOnly = await records.CreateAndAttachFieldAsync(company.Id, new("Registration", FieldTypes.Text, false));

        RecordDetails keep = await records.CreateRecordAsync(person.Id, "Ada", []);
        RecordDetails lose = await records.CreateRecordAsync(company.Id, "Ada Lovelace Ltd",
            [new(shared.Id, "555-0100"), new(companyOnly.Id, "SC123456")]);

        RecordMergePreview preview = Assert.IsType<RecordMergePreview>(
            await records.PreviewRecordMergeAsync(keep.Record.Id, lose.Record.Id, null));

        // A field the survivor's type does not carry has nowhere to live as record data. Saying so in
        // the preview is the difference between merging across types and losing something.
        RecordMergeUncarriedValue archived = Assert.Single(preview.Archived);
        Assert.Equal(companyOnly.Id, archived.FieldDefinitionId);
        Assert.Equal(RecordMergeUncarriedReasons.FieldNotOnSurvivingType, archived.Reason);

        await records.MergeRecordsAsync(keep.Record.Id, lose.Record.Id, [], preview.Revision);

        RecordDetails merged = Assert.IsType<RecordDetails>(await records.GetRecordAsync(keep.Record.Id));
        Assert.Equal(person.Id, merged.Record.RecordTypeId);
        Assert.Equal("555-0100", Assert.Single(merged.Values).TextValue);

        RecordSourceSnapshot sources = Assert.IsType<RecordSourceSnapshot>(
            await application.Services.GetRequiredService<IRecordSourceService>().GetAsync(keep.Record.Id));
        Assert.Contains("SC123456", sources.Values.Select(value => value.ValuePreview));
    }

    [Fact]
    public async Task AMergeRefusesToRunOnAnAgreementThatIsNoLongerTrue()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        FieldDefinition phone = await records.CreateAndAttachFieldAsync(person.Id, new("Phone", FieldTypes.Text, false));
        RecordDetails keep = await records.CreateRecordAsync(person.Id, "Ada", []);
        RecordDetails lose = await records.CreateRecordAsync(person.Id, "Ada L", [new(phone.Id, "555-0100")]);

        RecordMergePreview preview = Assert.IsType<RecordMergePreview>(
            await records.PreviewRecordMergeAsync(keep.Record.Id, lose.Record.Id, null));

        // Somebody edits the record that is about to be deleted. What the preview showed, and therefore
        // what anybody agreed to, is now wrong: the number they were told would be carried is not the
        // number that would be.
        await records.UpdateRecordAsync(lose.Record.Id, "Ada L", [new(phone.Id, "555-0999")]);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            records.MergeRecordsAsync(keep.Record.Id, lose.Record.Id, [], preview.Revision));
        Assert.NotNull(await records.GetRecordAsync(lose.Record.Id));
    }

    [Fact]
    public async Task APreviewOfARecordAgainstItselfSaysWhyRatherThanThrowing()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        RecordDetails ada = await records.CreateRecordAsync(person.Id, "Ada", []);

        // A picker asks about pairs, so it needs to be able to say why one is unavailable. Throwing would
        // make listing the options the same cost as a failure.
        RecordMergePreview preview = Assert.IsType<RecordMergePreview>(
            await records.PreviewRecordMergeAsync(ada.Record.Id, ada.Record.Id, null));
        Assert.NotNull(preview.Refusal);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            records.MergeRecordsAsync(ada.Record.Id, ada.Record.Id, [], preview.Revision));
        Assert.NotNull(await records.GetRecordAsync(ada.Record.Id));
    }

    [Fact]
    public async Task MergingARecordThatIsNotThereIsNotFoundRatherThanNothingHappening()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        RecordDetails ada = await records.CreateRecordAsync(person.Id, "Ada", []);

        Assert.Null(await records.PreviewRecordMergeAsync(ada.Record.Id, Guid.CreateVersion7(), null));
        await Assert.ThrowsAsync<RecordCommandNotFoundException>(() =>
            records.MergeRecordsAsync(ada.Record.Id, Guid.CreateVersion7(), [], "whatever"));
    }
}
