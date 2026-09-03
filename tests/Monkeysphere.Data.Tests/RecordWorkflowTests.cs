using DnaX.Data.Migrations;
using DnaX.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Monkeysphere.Core;
using Monkeysphere.Data;

namespace Monkeysphere.Data.Tests;

public sealed class RecordWorkflowTests
{
    [Fact]
    public async Task RecordSearchRejectsUnboundedInput()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService service = application.Services.GetRequiredService<IMonkeysphereService>();
        Guid fieldId = Guid.NewGuid();

        await Assert.ThrowsAsync<DomainValidationException>(() => service.SearchRecordsAsync(
            new RecordSearch(Query: new string('q', MonkeysphereService.MaximumSearchLength + 1))));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.SearchRecordsAsync(
            new RecordSearch(
                FieldDefinitionId: fieldId,
                Operator: FieldFilterOperator.Contains,
                FilterValue: new string('f', MonkeysphereService.MaximumFilterLength + 1))));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.SearchRecordsAsync(
            new RecordSearch(Filters: [new(fieldId, FieldFilterOperator.Contains, new string('f', MonkeysphereService.MaximumFilterLength + 1))])));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.SearchRecordsAsync(
            new RecordSearch(Page: MonkeysphereService.MaximumSearchPage + 1)));
    }

    [Fact]
    public async Task ConfigurableRecordRoundTripsSearchesUpdatesAndDeletes()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService service = application.Services.GetRequiredService<IMonkeysphereService>();

        RecordType type = await service.CreateRecordTypeAsync("Person");
        FieldDefinition nickname = await service.CreateAndAttachFieldAsync(
            type.Id,
            new CreateFieldRequest("Nickname", FieldTypes.Text, true));
        FieldDefinition score = await service.CreateAndAttachFieldAsync(
            type.Id,
            new CreateFieldRequest("Score", FieldTypes.Number, false));
        FieldDefinition birthday = await service.CreateAndAttachFieldAsync(
            type.Id,
            new CreateFieldRequest("Birthday", FieldTypes.ExactDate, false));
        FieldDefinition category = await service.CreateAndAttachFieldAsync(
            type.Id,
            new CreateFieldRequest("Category", FieldTypes.Choice, false, ["Friend", "Family"]));
        FieldDefinition tags = await service.CreateAndAttachFieldAsync(
            type.Id,
            new CreateFieldRequest("Tags", FieldTypes.Tags, false));
        FieldDefinition custom = await service.CreateAndAttachFieldAsync(
            type.Id,
            new CreateFieldRequest("Memory", "custom.memory", false));

        RecordDetails created = await service.CreateRecordAsync(type.Id, "Ada Lovelace", [
            new(nickname.Id, "Ada"),
            new(score.Id, "42.50"),
            new(birthday.Id, "1815-12-10"),
            new(category.Id, "friend"),
            new(tags.Id, Tags: ["mathematics", "Pioneer", "mathematics"]),
            new(custom.Id, "First programmer"),
        ], ["Augusta Ada King", "Enchantress of Numbers"]);

        RecordDetails reloaded = Assert.IsType<RecordDetails>(await service.GetRecordAsync(created.Record.Id));
        Assert.Equal("42.50", Assert.Single(reloaded.Values, item => item.FieldDefinitionId == score.Id).NumberValue);
        Assert.Equal("Friend", Assert.Single(reloaded.Values, item => item.FieldDefinitionId == category.Id).TextValue);
        Assert.Equal(["mathematics", "Pioneer"], Assert.Single(reloaded.Values, item => item.FieldDefinitionId == tags.Id).Tags);
        Assert.Equal("First programmer", Assert.Single(reloaded.Values, item => item.FieldDefinitionId == custom.Id).TextValue);
        Assert.Equal(["Augusta Ada King", "Enchantress of Numbers"], reloaded.Aliases);

        PagedResult<RecordSummary> textSearch = await service.SearchRecordsAsync(new RecordSearch(Query: "pioneer"));
        Assert.Equal(created.Record.Id, Assert.Single(textSearch.Items).Id);

        PagedResult<RecordSummary> aliasSearch = await service.SearchRecordsAsync(new RecordSearch(Query: "Enchantress"));
        Assert.Equal(created.Record.Id, Assert.Single(aliasSearch.Items).Id);

        PagedResult<RecordSummary> numericSearch = await service.SearchRecordsAsync(new RecordSearch(
            FieldDefinitionId: score.Id,
            Operator: FieldFilterOperator.GreaterThan,
            FilterValue: "40"));
        Assert.Equal(created.Record.Id, Assert.Single(numericSearch.Items).Id);

        PagedResult<RecordSummary> containsSearch = await service.SearchRecordsAsync(new RecordSearch(
            FieldDefinitionId: custom.Id,
            Operator: FieldFilterOperator.Contains,
            FilterValue: "program"));
        Assert.Equal(created.Record.Id, Assert.Single(containsSearch.Items).Id);

        RecordDetails updated = await service.UpdateRecordAsync(created.Record.Id, "Augusta Ada King", [
            new(nickname.Id, "Ada"),
            new(tags.Id, Tags: ["mathematics"]),
        ], ["Ada Lovelace"]);
        Assert.Equal("Augusta Ada King", updated.Record.DisplayName);
        Assert.Equal(["Ada Lovelace"], updated.Aliases);
        Assert.DoesNotContain(updated.Values, item => item.FieldDefinitionId == score.Id);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateRecordAsync(created.Record.Id, "Augusta Ada King", [], ["augusta ada king"]));

        Assert.True(await service.DeleteRecordAsync(created.Record.Id));
        Assert.Null(await service.GetRecordAsync(created.Record.Id));
        Assert.False(await service.DeleteRecordAsync(created.Record.Id));
    }

    [Fact]
    public async Task RecordImagesValidatePersistRenderAndDeleteWithTheirRecord()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRecordImageService images = application.Services.GetRequiredService<IRecordImageService>();
        IDnaXPaths paths = application.Services.GetRequiredService<IDnaXPaths>();
        RecordType type = await records.CreateRecordTypeAsync("Person with photos");
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Ada", []);
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        RecordImage first = await images.AddAsync(record.Record.Id, new MemoryStream(png), "../portrait.png");
        RecordImage second = await images.AddAsync(record.Record.Id, new MemoryStream(png), "another.png");

        Assert.Equal("portrait.png", first.OriginalFileName);
        Assert.Equal("image/png", first.OriginalContentType);
        Assert.Equal(0, first.Ordinal);
        Assert.True(first.IsCover);
        Assert.Equal(1, second.Ordinal);
        Assert.Equal([first.Id, second.Id], (await records.GetRecordAsync(record.Record.Id))!.Images.Select(image => image.Id));
        RecordImageFile thumbnail = Assert.IsType<RecordImageFile>(
            await images.OpenAsync(record.Record.Id, first.Id, RecordImageVariant.Thumbnail));
        await using (thumbnail.Content)
        {
            Assert.Equal("image/webp", thumbnail.ContentType);
            Assert.True(thumbnail.Content.Length > 0);
        }

        RecordImage captioned = await images.UpdateMetadataAsync(record.Record.Id, second.Id, "A second portrait", true);
        Assert.Equal("A second portrait", captioned.Caption);
        Assert.True(captioned.IsCover);
        await images.ReorderAsync(record.Record.Id, [second.Id, first.Id]);
        IReadOnlyList<RecordImage> reordered = (await records.GetRecordAsync(record.Record.Id))!.Images;
        Assert.Equal([second.Id, first.Id], reordered.Select(image => image.Id));

        RecordImage corrected = await images.CorrectAsync(record.Record.Id, second.Id, new ImageCorrection(1));
        Assert.Equal(1, corrected.Correction?.RotationQuarterTurns);
        await Assert.ThrowsAsync<DomainValidationException>(() => images.CorrectAsync(
            record.Record.Id,
            second.Id,
            new ImageCorrection(CropX: 1, CropY: 0, CropWidth: 1, CropHeight: 1)));
        RecordImageFile original = Assert.IsType<RecordImageFile>(
            await images.OpenAsync(record.Record.Id, second.Id, RecordImageVariant.Original));
        await using (original.Content)
        {
            Assert.Equal("image/png", original.ContentType);
            Assert.Equal("another.png", original.DownloadFileName);
            using MemoryStream copy = new();
            await original.Content.CopyToAsync(copy);
            Assert.Equal(png, copy.ToArray());
        }

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            images.AddAsync(record.Record.Id, new MemoryStream("not an image"u8.ToArray()), "fake.jpg"));
        Assert.True(await images.DeleteAsync(record.Record.Id, second.Id));
        Assert.False(await images.DeleteAsync(record.Record.Id, second.Id));
        RecordImage remaining = Assert.Single((await records.GetRecordAsync(record.Record.Id))!.Images);
        Assert.Equal(first.Id, remaining.Id);
        Assert.Equal(0, remaining.Ordinal);
        Assert.True(remaining.IsCover);

        string mediaDirectory = paths.ResolveWritable(Path.Combine("media", "records", record.Record.Id.ToString("N")));
        Assert.True(Directory.Exists(mediaDirectory));
        Assert.True(await records.DeleteRecordAsync(record.Record.Id));
        Assert.False(Directory.Exists(mediaDirectory));
        Assert.Null(await images.OpenAsync(record.Record.Id, first.Id, RecordImageVariant.Preview));
    }

    [Fact]
    public async Task StructuredLocationRoundTripsSearchesFiltersAndUpdates()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService service = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType type = await service.CreateRecordTypeAsync("Place");
        FieldDefinition location = await service.CreateAndAttachFieldAsync(
            type.Id,
            new CreateFieldRequest("Location", FieldTypes.Location, true));

        RecordDetails created = await service.CreateRecordAsync(type.Id, "Favourite viewpoint", [
            new(location.Id, Location: new LocationValueInput(
                "Central London",
                "51.50740004",
                "-0.12780006",
                "12.5",
                "5")),
        ]);
        LocationValue stored = Assert.IsType<LocationValue>(Assert.Single(created.Values).Location);
        Assert.Equal(51.5074, stored.Latitude);
        Assert.Equal(-0.1278001, stored.Longitude);
        Assert.Equal(5, stored.ApproximationRadiusKilometres);
        Assert.Equal(created.Record.Id, Assert.Single((await service.SearchRecordsAsync(
            new RecordSearch(Query: "Central London"))).Items).Id);
        Assert.Equal(created.Record.Id, Assert.Single((await service.SearchRecordsAsync(new RecordSearch(
            FieldDefinitionId: location.Id,
            Operator: FieldFilterOperator.Contains,
            FilterValue: "london"))).Items).Id);

        RecordDetails updated = await service.UpdateRecordAsync(created.Record.Id, created.Record.DisplayName, [
            new(location.Id, Location: new LocationValueInput(
                "Somewhere near London",
                ApproximationRadiusKilometres: "20")),
        ]);
        LocationValue approximate = Assert.IsType<LocationValue>(Assert.Single(updated.Values).Location);
        Assert.Null(approximate.Latitude);
        Assert.Equal(20, approximate.ApproximationRadiusKilometres);
        await Assert.ThrowsAsync<DomainValidationException>(() => service.UpdateRecordAsync(
            created.Record.Id,
            created.Record.DisplayName,
            [new(location.Id, Location: new LocationValueInput())]));
    }

    [Fact]
    public async Task SpatialMapQueriesCoordinatesWithFiltersBoundsAndPagination()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        ISpatialMapService maps = application.Services.GetRequiredService<ISpatialMapService>();
        RecordType place = await records.CreateRecordTypeAsync("Mapped place");
        FieldDefinition location = await records.CreateAndAttachFieldAsync(
            place.Id,
            new CreateFieldRequest("Location", FieldTypes.Location, false));
        _ = await records.CreateRecordAsync(place.Id, "London", [
            new(location.Id, Location: new LocationValueInput("Central London", "51.5074", "-0.1278")),
        ]);
        _ = await records.CreateRecordAsync(place.Id, "Tokyo", [
            new(location.Id, Location: new LocationValueInput("Central Tokyo", "35.6762", "139.6503")),
        ]);
        _ = await records.CreateRecordAsync(place.Id, "Near London", [
            new(location.Id, Location: new LocationValueInput("Approximate area", "51.5", "2", ApproximationRadiusKilometres: "150")),
        ]);
        _ = await records.CreateRecordAsync(place.Id, "Unknown", [
            new(location.Id, Location: new LocationValueInput("Somewhere", ApproximationRadiusKilometres: "25")),
        ]);

        PagedResult<SpatialMapEntry> world = await maps.QueryAsync(new(PageSize: 1));
        Assert.Equal(3, world.TotalCount);
        Assert.Single(world.Items);
        PagedResult<SpatialMapEntry> london = await maps.QueryAsync(new(
            South: 50,
            West: -1,
            North: 52,
            East: 1,
            RecordTypeId: place.Id,
            FieldDefinitionId: location.Id));
        Assert.Equal(2, london.Items.Count);
        SpatialMapEntry entry = Assert.Single(london.Items, item => item.RecordDisplayName == "London");
        Assert.Equal("London", entry.RecordDisplayName);
        Assert.Equal("Central London", entry.DisplayContext);
        Assert.Equal(51.5074, entry.Latitude);
        SpatialMapEntry approximate = Assert.Single(london.Items, item => item.RecordDisplayName == "Near London");
        Assert.Equal(150, approximate.ApproximationRadiusKilometres);
        PagedResult<SpatialMapEntry> noMatchingLayer = await maps.QueryAsync(new(
            FieldDefinitionIds: [Guid.NewGuid()]));
        Assert.Empty(noMatchingLayer.Items);
    }

    [Fact]
    public async Task RequiredAndChoiceValidationHappensBeforeStorage()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService service = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType type = await service.CreateRecordTypeAsync("Pet");
        FieldDefinition name = await service.CreateAndAttachFieldAsync(
            type.Id,
            new CreateFieldRequest("Name", FieldTypes.Text, true));
        FieldDefinition kind = await service.CreateAndAttachFieldAsync(
            type.Id,
            new CreateFieldRequest("Kind", FieldTypes.Choice, false, ["Cat", "Dog"]));

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateRecordAsync(type.Id, "Unnamed", []));
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateRecordAsync(type.Id, "Milo", [new(name.Id, "Milo"), new(kind.Id, "Rabbit")]));

        PagedResult<RecordSummary> results = await service.SearchRecordsAsync(new RecordSearch());
        Assert.Empty(results.Items);
    }

    [Fact]
    public async Task FieldDefinitionCanBeRenamedAndReusedWithoutChangingIdentity()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService service = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType person = await service.CreateRecordTypeAsync("Person");
        RecordType organisation = await service.CreateRecordTypeAsync("Organisation");
        FieldDefinition notes = await service.CreateAndAttachFieldAsync(
            person.Id,
            new CreateFieldRequest("Notes", FieldTypes.MultilineText, false));

        await service.AttachFieldAsync(organisation.Id, notes.Id, isRequired: true);
        await service.RenameFieldAsync(notes.Id, "Shared notes");

        RecordTypeDetails personDetails = Assert.IsType<RecordTypeDetails>(await service.GetRecordTypeAsync(person.Id));
        RecordTypeDetails organisationDetails = Assert.IsType<RecordTypeDetails>(await service.GetRecordTypeAsync(organisation.Id));
        RecordTypeField personField = Assert.Single(personDetails.Fields);
        RecordTypeField organisationField = Assert.Single(organisationDetails.Fields);
        Assert.Equal(notes.Id, personField.Definition.Id);
        Assert.Equal(notes.Id, organisationField.Definition.Id);
        Assert.Equal("Shared notes", personField.Definition.Name);
        Assert.Equal("Shared notes", organisationField.Definition.Name);
        Assert.False(personField.IsRequired);
        Assert.True(organisationField.IsRequired);

        IReadOnlyList<FieldDefinition> definitions = await service.ListFieldDefinitionsAsync();
        Assert.Equal(notes.Id, Assert.Single(definitions).Id);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AttachFieldAsync(organisation.Id, notes.Id, isRequired: false));
    }

    [Fact]
    public async Task RetiredExistingValuesRemainEditableButCannotBeAddedToNewRecords()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService service = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType person = await service.CreateRecordTypeAsync("Person");
        FieldDefinition note = await service.CreateAndAttachFieldAsync(
            person.Id,
            new CreateFieldRequest("Legacy note", "custom.note", false));
        RecordDetails existing = await service.CreateRecordAsync(person.Id, "Ada", [new(note.Id, "  original  ")]);

        await service.RetireFieldAsync(note.Id);
        RecordDetails reloaded = Assert.IsType<RecordDetails>(await service.GetRecordAsync(existing.Record.Id));
        Assert.Equal("  original  ", Assert.Single(reloaded.Values).TextValue);

        RecordDetails corrected = await service.UpdateRecordAsync(existing.Record.Id, "Ada", [new(note.Id, "  corrected  ")]);
        Assert.Equal("  corrected  ", Assert.Single(corrected.Values).TextValue);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateRecordAsync(person.Id, "New record", [new(note.Id, "not allowed")]));
    }

    [Fact]
    public async Task CompatibleFieldMergePreviewsConflictsAndMovesEveryReferenceTransactionally()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        ISavedViewService views = application.Services.GetRequiredService<ISavedViewService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        FieldDefinition source = await records.CreateAndAttachFieldAsync(
            person.Id,
            new CreateFieldRequest("Nickname", FieldTypes.Text, true));
        FieldDefinition target = await records.CreateAndAttachFieldAsync(
            person.Id,
            new CreateFieldRequest("Preferred name", FieldTypes.Text, false));
        RecordDetails sourceOnly = await records.CreateRecordAsync(
            person.Id,
            "Ada",
            [new(source.Id, "Enchantress of Numbers")]);
        RecordDetails conflict = await records.CreateRecordAsync(
            person.Id,
            "Grace",
            [new(source.Id, "Amazing Grace"), new(target.Id, "Grace")]);
        SavedViewDetails saved = await views.CreateAsync(new SaveViewRequest(
            "Nicknames",
            person.Id,
            null,
            [source.Id, target.Id],
            [new(source.Id, FieldFilterOperator.Contains, "Grace")],
            source.Id,
            source.Id));

        FieldMergePreview preview = await records.PreviewFieldMergeAsync(source.Id, target.Id);

        Assert.True(preview.IsCompatible);
        Assert.Equal(2, preview.SourceValueCount);
        Assert.Equal(1, preview.ConflictingValueCount);
        Assert.Equal(4, preview.SavedViewReferenceCount);
        await Assert.ThrowsAsync<DomainValidationException>(() => records.MergeFieldsAsync(
            source.Id,
            target.Id,
            FieldMergeConflictResolution.Reject,
            preview.Revision));

        await records.UpdateRecordAsync(
            sourceOnly.Record.Id,
            "Ada",
            [new(source.Id, "Enchantress")]);
        await Assert.ThrowsAsync<DomainValidationException>(() => records.MergeFieldsAsync(
            source.Id,
            target.Id,
            FieldMergeConflictResolution.KeepSource,
            preview.Revision));
        preview = await records.PreviewFieldMergeAsync(source.Id, target.Id);
        await records.MergeFieldsAsync(source.Id, target.Id, FieldMergeConflictResolution.KeepSource, preview.Revision);

        RecordTypeDetails type = Assert.IsType<RecordTypeDetails>(await records.GetRecordTypeAsync(person.Id));
        RecordTypeField attachment = Assert.Single(type.Fields, field => field.Definition.Id == target.Id);
        Assert.True(attachment.IsRequired);
        Assert.DoesNotContain(type.Fields, field => field.Definition.Id == source.Id);
        Assert.Equal("Enchantress", Assert.Single(
            (await records.GetRecordAsync(sourceOnly.Record.Id))!.Values).TextValue);
        Assert.Equal("Amazing Grace", Assert.Single(
            (await records.GetRecordAsync(conflict.Record.Id))!.Values).TextValue);
        Assert.Equal(FieldLifecycle.Retired, Assert.Single(
            await records.ListFieldDefinitionsAsync(), field => field.Id == source.Id).Lifecycle);

        SavedViewDetails updatedView = Assert.IsType<SavedViewDetails>(await views.GetAsync(saved.View.Id));
        Assert.Equal([target.Id], updatedView.ColumnFieldDefinitionIds);
        Assert.All(updatedView.Filters, filter => Assert.Equal(target.Id, filter.FieldDefinitionId));
        Assert.Equal(target.Id, updatedView.View.GroupByFieldDefinitionId);
        Assert.Equal(target.Id, updatedView.View.SortFieldDefinitionId);
    }

    [Fact]
    public async Task FieldConversionRefusesLossThenCreatesANewDefinitionAndMigratesValues()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        FieldDefinition source = await records.CreateAndAttachFieldAsync(
            person.Id,
            new CreateFieldRequest("Score as text", FieldTypes.Text, false));
        RecordDetails valid = await records.CreateRecordAsync(person.Id, "Ada", [new(source.Id, "12.5")]);
        RecordDetails invalid = await records.CreateRecordAsync(person.Id, "Grace", [new(source.Id, "excellent")]);
        ConvertFieldRequest request = new("Score", FieldTypes.Number);

        FieldConversionPreview blocked = await records.PreviewFieldConversionAsync(source.Id, request);

        Assert.Equal(2, blocked.ValueCount);
        Assert.Equal(1, blocked.FailedValueCount);
        Assert.Equal("Grace", Assert.Single(blocked.Issues).RecordDisplayName);
        await Assert.ThrowsAsync<DomainValidationException>(() => records.ConvertFieldAsync(source.Id, request, blocked.Revision));
        Assert.Single(await records.ListFieldDefinitionsAsync());

        await records.UpdateRecordAsync(invalid.Record.Id, "Grace", [new(source.Id, "13.75")]);
        FieldConversionPreview ready = await records.PreviewFieldConversionAsync(source.Id, request);
        Assert.Equal(0, ready.FailedValueCount);

        await records.UpdateRecordAsync(valid.Record.Id, "Ada", [new(source.Id, "12.75")]);
        await Assert.ThrowsAsync<DomainValidationException>(() => records.ConvertFieldAsync(source.Id, request, ready.Revision));
        ready = await records.PreviewFieldConversionAsync(source.Id, request);

        FieldDefinition target = await records.ConvertFieldAsync(source.Id, request, ready.Revision);

        Assert.NotEqual(source.Id, target.Id);
        Assert.Equal(FieldTypes.Number, target.TypeId);
        RecordTypeDetails type = Assert.IsType<RecordTypeDetails>(await records.GetRecordTypeAsync(person.Id));
        Assert.Equal(target.Id, Assert.Single(type.Fields).Definition.Id);
        Assert.Equal("12.75", Assert.Single((await records.GetRecordAsync(valid.Record.Id))!.Values).NumberValue);
        Assert.Equal("13.75", Assert.Single((await records.GetRecordAsync(invalid.Record.Id))!.Values).NumberValue);
        Assert.Equal(FieldLifecycle.Retired, Assert.Single(
            await records.ListFieldDefinitionsAsync(), field => field.Id == source.Id).Lifecycle);
    }

    [Fact]
    public async Task FieldMergeRequiresMatchingTypeAndConfiguration()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        FieldDefinition source = await records.CreateAndAttachFieldAsync(
            person.Id,
            new CreateFieldRequest("Kind", FieldTypes.Choice, false, ["Friend", "Family"]));
        FieldDefinition target = await records.CreateAndAttachFieldAsync(
            person.Id,
            new CreateFieldRequest("Category", FieldTypes.Choice, false, ["Friend", "Work"]));

        FieldMergePreview preview = await records.PreviewFieldMergeAsync(source.Id, target.Id);

        Assert.False(preview.IsCompatible);
        Assert.Contains("configurations", preview.IncompatibilityReason, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<DomainValidationException>(() => records.MergeFieldsAsync(
            source.Id,
            target.Id,
            FieldMergeConflictResolution.KeepTarget,
            preview.Revision));
    }

    [Fact]
    public async Task TagsRejectEmptyAndOversizedValuesAndDeduplicateSafely()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService service = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType person = await service.CreateRecordTypeAsync("Person");
        FieldDefinition tags = await service.CreateAndAttachFieldAsync(
            person.Id,
            new CreateFieldRequest("Tags", FieldTypes.Tags, false));

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateRecordAsync(person.Id, "Empty", [new(tags.Id, Tags: ["valid", " "])]));
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateRecordAsync(person.Id, "Long", [new(tags.Id, Tags: [new string('x', 201)])]));

        RecordDetails deduplicated = await service.CreateRecordAsync(
            person.Id,
            "Duplicate",
            [new(tags.Id, Tags: ["Friend", "friend"])]);
        Assert.Equal(["Friend"], Assert.Single(deduplicated.Values).Tags);
    }

    [Fact]
    public async Task PrecisionAwareTemporalValueRoundTripsAndParticipatesInSearch()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService service = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType eventType = await service.CreateRecordTypeAsync("Event");
        FieldDefinition when = await service.CreateAndAttachFieldAsync(
            eventType.Id,
            new CreateFieldRequest("When", FieldTypes.Temporal, true));

        RecordDetails created = await service.CreateRecordAsync(eventType.Id, "An uncertain event", [
            new(when.Id, Temporal: new TemporalValueInput("1810s", TemporalPrecision.Decade, true, "estimated from a letter")),
        ]);

        RecordValue value = Assert.Single(created.Values);
        Assert.Equal("1810", value.TemporalValue);
        Assert.Equal(TemporalPrecision.Decade, value.TemporalPrecision);
        Assert.Equal("1810-01-01T00:00:00", value.TemporalSortKey);
        Assert.True(value.IsApproximate);
        Assert.Equal("estimated from a letter", value.ApproximationNote);

        PagedResult<RecordSummary> textSearch = await service.SearchRecordsAsync(new RecordSearch(Query: "letter"));
        Assert.Equal(created.Record.Id, Assert.Single(textSearch.Items).Id);

        PagedResult<RecordSummary> dateSearch = await service.SearchRecordsAsync(new RecordSearch(
            FieldDefinitionId: when.Id,
            Operator: FieldFilterOperator.Before,
            FilterValue: "1820-01-01"));
        Assert.Equal(created.Record.Id, Assert.Single(dateSearch.Items).Id);
    }

    [Fact]
    public async Task PhoneAndWebLinkFieldsValidateAndRoundTrip()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService service = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType person = await service.CreateRecordTypeAsync("Contact");
        FieldDefinition phone = await service.CreateAndAttachFieldAsync(
            person.Id,
            new CreateFieldRequest("Phone", FieldTypes.PhoneNumber, false));
        FieldDefinition website = await service.CreateAndAttachFieldAsync(
            person.Id,
            new CreateFieldRequest("Website", FieldTypes.WebLink, false));

        RecordDetails record = await service.CreateRecordAsync(person.Id, "A contact", [
            new(phone.Id, "+44 (0)20 1234 5678"),
            new(website.Id, "https://example.test/profile"),
        ]);
        Assert.Equal("+44 (0)20 1234 5678", Assert.Single(record.Values, item => item.FieldDefinitionId == phone.Id).TextValue);
        Assert.Equal("https://example.test/profile", Assert.Single(record.Values, item => item.FieldDefinitionId == website.Id).TextValue);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateRecordAsync(person.Id, "Bad phone", [new(phone.Id, "call me")]));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateRecordAsync(person.Id, "Bad link", [new(website.Id, "file:///private.txt")]));
    }

    [Fact]
    public async Task DuplicateRecordTypeNameProducesDomainValidationError()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService service = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType person = await service.CreateRecordTypeAsync("Person", "👤");
        RecordType pet = await service.CreateRecordTypeAsync("Pet");

        Assert.Equal("👤", person.Symbol);
        await service.UpdateRecordTypeAsync(pet.Id, "Pet", "🐾");
        Assert.Equal("🐾", (await service.GetRecordTypeAsync(pet.Id))?.RecordType.Symbol);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateRecordTypeAsync(pet.Id, "Pet", "ABCDE"));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateRecordTypeAsync("person"));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.RenameRecordTypeAsync(pet.Id, person.Name));
        await service.RenameRecordTypeAsync(pet.Id, "Companion");
        Assert.Equal("🐾", (await service.GetRecordTypeAsync(pet.Id))?.RecordType.Symbol);
    }

    [Fact]
    public async Task RecordTypeRetirementPreservesExistingWorkAndRejectsStalePreviews()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        ISavedViewService views = application.Services.GetRequiredService<ISavedViewService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        FieldDefinition name = await records.CreateAndAttachFieldAsync(
            person.Id,
            new CreateFieldRequest("Known as", FieldTypes.Text, true));
        RecordDetails ada = await records.CreateRecordAsync(person.Id, "Ada", [new(name.Id, "Ada")]);
        SavedViewDetails view = await views.CreateAsync(new SaveViewRequest(
            "People",
            person.Id,
            null,
            [name.Id],
            []));

        RecordTypeRetirementPreview preview = await records.PreviewRecordTypeRetirementAsync(person.Id);
        Assert.Equal(1, preview.RecordCount);
        Assert.Equal(1, preview.SavedViewCount);

        await records.UpdateRecordAsync(ada.Record.Id, "Ada Lovelace", [new(name.Id, "Ada")]);
        await Assert.ThrowsAsync<DomainValidationException>(() => records.RetireRecordTypeAsync(person.Id, preview.Revision));
        preview = await records.PreviewRecordTypeRetirementAsync(person.Id);
        await records.RetireRecordTypeAsync(person.Id, preview.Revision);

        RecordType retired = Assert.Single(await records.ListRecordTypesAsync());
        Assert.Equal(RecordTypeLifecycle.Retired, retired.Lifecycle);
        Assert.NotNull(await views.GetAsync(view.View.Id));
        RecordDetails corrected = await records.UpdateRecordAsync(
            ada.Record.Id,
            "Augusta Ada King",
            [new(name.Id, "Ada")]);
        Assert.Equal("Augusta Ada King", corrected.Record.DisplayName);
        await Assert.ThrowsAsync<DomainValidationException>(() => records.CreateRecordAsync(
            person.Id,
            "New person",
            [new(name.Id, "New")]));
    }

    [Fact]
    public async Task RecordTypeMergeMovesRecordsAndViewsWhilePreservingValidFieldRules()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        ISavedViewService views = application.Services.GetRequiredService<ISavedViewService>();
        RecordType source = await records.CreateRecordTypeAsync("Contact");
        RecordType target = await records.CreateRecordTypeAsync("Person");
        FieldDefinition shared = await records.CreateAndAttachFieldAsync(
            source.Id,
            new CreateFieldRequest("Notes", FieldTypes.Text, true));
        await records.AttachFieldAsync(target.Id, shared.Id, isRequired: false);
        FieldDefinition sourceOnly = await records.CreateAndAttachFieldAsync(
            source.Id,
            new CreateFieldRequest("Legacy code", FieldTypes.Text, true));
        FieldDefinition targetOnly = await records.CreateAndAttachFieldAsync(
            target.Id,
            new CreateFieldRequest("Preferred name", FieldTypes.Text, true));
        RecordDetails sourceRecord = await records.CreateRecordAsync(
            source.Id,
            "Ada",
            [new(shared.Id, "Mathematician"), new(sourceOnly.Id, "A-1")]);
        _ = await records.CreateRecordAsync(target.Id, "Grace", [new(targetOnly.Id, "Grace")]);
        SavedViewDetails sourceView = await views.CreateAsync(new SaveViewRequest(
            "Contacts",
            source.Id,
            null,
            [sourceOnly.Id],
            []));

        RecordTypeMergePreview preview = await records.PreviewRecordTypeMergeAsync(source.Id, target.Id);
        Assert.Equal(1, preview.SourceRecordCount);
        Assert.Equal(1, preview.TargetRecordCount);
        Assert.Equal(1, preview.SourceSavedViewCount);
        Assert.Equal(2, preview.SourceFieldCount);
        Assert.Equal(1, preview.SharedFieldCount);
        Assert.Equal(1, preview.AddedFieldCount);
        Assert.Equal(3, preview.RequiredDowngradeCount);

        _ = await records.CreateRecordAsync(
            source.Id,
            "Charles",
            [new(shared.Id, "Inventor"), new(sourceOnly.Id, "C-1")]);
        await Assert.ThrowsAsync<DomainValidationException>(() => records.MergeRecordTypesAsync(
            source.Id,
            target.Id,
            preview.Revision));
        preview = await records.PreviewRecordTypeMergeAsync(source.Id, target.Id);
        await records.MergeRecordTypesAsync(source.Id, target.Id, preview.Revision);

        RecordTypeDetails sourceDetails = Assert.IsType<RecordTypeDetails>(await records.GetRecordTypeAsync(source.Id));
        RecordTypeDetails targetDetails = Assert.IsType<RecordTypeDetails>(await records.GetRecordTypeAsync(target.Id));
        Assert.Equal(RecordTypeLifecycle.Retired, sourceDetails.RecordType.Lifecycle);
        Assert.Equal(RecordTypeLifecycle.Active, targetDetails.RecordType.Lifecycle);
        Assert.Equal(2, sourceDetails.Fields.Count);
        Assert.Equal(3, targetDetails.Fields.Count);
        Assert.All(targetDetails.Fields, field => Assert.False(field.IsRequired));
        Assert.Equal(target.Id, (await records.GetRecordAsync(sourceRecord.Record.Id))!.Record.RecordTypeId);
        Assert.Equal(target.Id, (await views.GetAsync(sourceView.View.Id))!.View.RecordTypeId);
        Assert.Equal(target.Id, targetDetails.RecordType.Id);
    }

    [Fact]
    public async Task SavedViewRoundTripsMultipleFiltersColumnsGroupingSortingAndLifecycle()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        ISavedViewService views = application.Services.GetRequiredService<ISavedViewService>();
        RecordType person = await records.CreateRecordTypeAsync("Saved-view person");
        FieldDefinition score = await records.CreateAndAttachFieldAsync(
            person.Id,
            new CreateFieldRequest("Score", FieldTypes.Number, false));
        FieldDefinition category = await records.CreateAndAttachFieldAsync(
            person.Id,
            new CreateFieldRequest("Category", FieldTypes.Choice, false, ["Friend", "Family"]));

        RecordDetails ada = await records.CreateRecordAsync(person.Id, "Ada", [
            new(score.Id, "42"),
            new(category.Id, "Friend"),
        ]);
        RecordDetails grace = await records.CreateRecordAsync(person.Id, "Grace", [
            new(score.Id, "84"),
            new(category.Id, "Friend"),
        ]);
        _ = await records.CreateRecordAsync(person.Id, "Charles", [
            new(score.Id, "100"),
            new(category.Id, "Family"),
        ]);

        SavedViewDetails created = await views.CreateAsync(new SaveViewRequest(
            "High-scoring friends",
            person.Id,
            Query: null,
            ColumnFieldDefinitionIds: [category.Id, score.Id],
            Filters:
            [
                new(score.Id, FieldFilterOperator.GreaterThan, "40"),
                new(category.Id, FieldFilterOperator.Equals, "friend"),
            ],
            GroupByFieldDefinitionId: category.Id,
            SortFieldDefinitionId: score.Id,
            SortDescending: true));

        Assert.Equal([category.Id, score.Id], created.ColumnFieldDefinitionIds);
        Assert.Equal(2, created.Filters.Count);
        Assert.Equal(created.View.Id, Assert.Single(await views.ListAsync()).Id);

        PagedResult<RecordSummary> results = await records.SearchRecordsAsync(views.ToSearch(created));
        Assert.Equal([grace.Record.Id, ada.Record.Id], results.Items.Select(item => item.Id));

        await records.RenameFieldAsync(category.Id, "Circle");
        await records.RetireFieldAsync(score.Id);
        SavedViewDetails afterLifecycleChange = Assert.IsType<SavedViewDetails>(await views.GetAsync(created.View.Id));
        Assert.Contains(score.Id, afterLifecycleChange.ColumnFieldDefinitionIds);
        Assert.Equal(2, (await records.SearchRecordsAsync(views.ToSearch(afterLifecycleChange))).TotalCount);

        SavedViewDetails updated = await views.UpdateAsync(created.View.Id, new SaveViewRequest(
            "Friends by score",
            person.Id,
            null,
            [score.Id],
            [new(category.Id, FieldFilterOperator.Equals, "Friend")],
            SortFieldDefinitionId: score.Id,
            SortDescending: false));
        Assert.Equal("Friends by score", updated.View.Name);
        Assert.Single(updated.Filters);

        SavedViewDetails duplicate = await views.DuplicateAsync(updated.View.Id, "Friends by score copy");
        Assert.NotEqual(updated.View.Id, duplicate.View.Id);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            views.DuplicateAsync(updated.View.Id, "Friends by score copy"));

        Assert.True(await views.DeleteAsync(duplicate.View.Id));
        Assert.False(await views.DeleteAsync(duplicate.View.Id));
    }

    [Fact]
    public async Task CalendarIncludesOnlyExactAndNonApproximateDayValuesAndSupportsFilters()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        ICalendarService calendar = application.Services.GetRequiredService<ICalendarService>();

        RecordType person = await records.CreateRecordTypeAsync("Person");
        RecordType eventType = await records.CreateRecordTypeAsync("Event");
        FieldDefinition birthday = await records.CreateAndAttachFieldAsync(
            person.Id,
            new CreateFieldRequest("Birthday", FieldTypes.ExactDate, false));
        FieldDefinition when = await records.CreateAndAttachFieldAsync(
            eventType.Id,
            new CreateFieldRequest("When", FieldTypes.Temporal, false));

        RecordDetails exact = await records.CreateRecordAsync(person.Id, "Ada", [new(birthday.Id, "2026-09-01")]);
        RecordDetails day = await records.CreateRecordAsync(eventType.Id, "Conference", [
            new(when.Id, Temporal: new TemporalValueInput("2026-09-03", TemporalPrecision.Day))]);
        RecordDetails approximate = await records.CreateRecordAsync(eventType.Id, "Estimated meeting", [
            new(when.Id, Temporal: new TemporalValueInput("2026-09-04", TemporalPrecision.Day, true, "Diary estimate"))]);
        _ = await records.CreateRecordAsync(eventType.Id, "Month only", [
            new(when.Id, Temporal: new TemporalValueInput("2026-09", TemporalPrecision.Month))]);

        IReadOnlyList<CalendarEntry> all = await calendar.QueryAsync(new(
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 30)));
        Assert.Equal([exact.Record.Id, day.Record.Id], all.Select(entry => entry.RecordId));

        CalendarEntry filtered = Assert.Single(await calendar.QueryAsync(new(
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 30),
            person.Id,
            birthday.Id)));
        Assert.Equal("Birthday", filtered.FieldName);
        Assert.Equal(new DateOnly(2026, 9, 1), filtered.Date);

        IReminderService reminders = application.Services.GetRequiredService<IReminderService>();
        Reminder reminder = await reminders.CreateAsync(filtered.FieldValueId, 7);
        ReminderItem scheduled = Assert.Single(await reminders.ListActiveAsync());
        Assert.Equal(reminder.Id, scheduled.Reminder.Id);
        Assert.Equal(new DateOnly(2026, 8, 25), scheduled.DueDate);
        await Assert.ThrowsAsync<DomainValidationException>(() => reminders.CreateAsync(filtered.FieldValueId, 7));
        await Assert.ThrowsAsync<DomainValidationException>(() => reminders.CreateAsync(
            Assert.Single(approximate.Values).Id,
            7));

        RecordDetails updatedExact = await records.UpdateRecordAsync(
            exact.Record.Id,
            "Ada",
            [new(birthday.Id, "2026-09-02")]);
        scheduled = Assert.Single(await reminders.ListActiveAsync());
        Assert.Equal(new DateOnly(2026, 9, 2), scheduled.Entry.Date);
        Assert.Equal(new DateOnly(2026, 8, 26), scheduled.DueDate);
        Assert.True(await reminders.DismissAsync(reminder.Id));
        Assert.Empty(await reminders.ListActiveAsync());

        _ = await reminders.CreateAsync(Assert.Single(updatedExact.Values).Id, 1);
        await records.UpdateRecordAsync(exact.Record.Id, "Ada", []);
        Assert.Empty(await reminders.ListActiveAsync());

        await Assert.ThrowsAsync<DomainValidationException>(() => calendar.QueryAsync(new(
            new DateOnly(2026, 9, 2),
            new DateOnly(2026, 9, 1))));
    }

    [Fact]
    public async Task DashboardPersistsCategoryAndProjectsAnnualDatesWithDayAndTimePrecision()
    {
        FixedTimeProvider clock = new(new DateTimeOffset(2026, 9, 3, 10, 30, 0, TimeSpan.Zero));
        await using TestApplication application = await TestApplication.CreateAsync(clock);
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IDashboardService dashboard = application.Services.GetRequiredService<IDashboardService>();

        RecordType person = await records.CreateRecordTypeAsync("Person");
        RecordType pet = await records.CreateRecordTypeAsync("Pet");
        FieldDefinition birthday = await records.CreateAndAttachFieldAsync(
            person.Id,
            new CreateFieldRequest("Birthday", FieldTypes.ExactDate, false));
        FieldDefinition anniversary = await records.CreateAndAttachFieldAsync(
            person.Id,
            new CreateFieldRequest("Anniversary", FieldTypes.Temporal, false));
        FieldDefinition notes = await records.CreateAndAttachFieldAsync(
            person.Id,
            new CreateFieldRequest("Notes", FieldTypes.Text, false));

        _ = await records.CreateRecordAsync(person.Id, "Ada", [new(birthday.Id, "1815-09-05")]);
        _ = await records.CreateRecordAsync(person.Id, "Grace", [
            new(anniversary.Id, Temporal: new TemporalValueInput("1994-09-03T15:00", TemporalPrecision.Minute)),
        ]);

        DashboardConfiguration saved = await dashboard.SaveConfigurationAsync(new(
            [pet.Id, person.Id],
            [birthday.Id, anniversary.Id],
            30));
        DashboardConfiguration reloaded = await dashboard.GetConfigurationAsync();
        Assert.Equal(saved.RecordTypeIds, reloaded.RecordTypeIds);
        Assert.Equal([pet.Id, person.Id], reloaded.RecordTypeIds);
        Assert.Equal(saved.UpcomingDays, reloaded.UpcomingDays);
        Assert.Equal(saved.RecurringFieldDefinitionIds.ToArray(), reloaded.RecurringFieldDefinitionIds.ToArray());

        IReadOnlyList<DashboardUpcomingDate> upcoming = await dashboard.ListUpcomingAsync();
        Assert.Equal(["Grace", "Ada"], upcoming.Select(item => item.Source.RecordDisplayName));
        Assert.True(upcoming[0].HasTime);
        Assert.Equal(new DateTimeOffset(2026, 9, 3, 15, 0, 0, TimeSpan.Zero), upcoming[0].OccursAt);
        Assert.False(upcoming[1].HasTime);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero), upcoming[1].OccursAt);

        await Assert.ThrowsAsync<DomainValidationException>(() => dashboard.SaveConfigurationAsync(new(
            [person.Id],
            [notes.Id],
            30)));
    }

    [Fact]
    public async Task DebugResetReturnsTheApplicationToAnEmptyFirstRunState()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IPresetService presets = application.Services.GetRequiredService<IPresetService>();
        IDebugDatabaseResetService reset = application.Services.GetRequiredService<IDebugDatabaseResetService>();

        RecordType type = await records.CreateRecordTypeAsync("Temporary");
        _ = await records.CreateRecordAsync(type.Id, "Delete me", []);

        await reset.ResetAsync();

        Assert.Empty(await records.ListRecordTypesAsync());
        Assert.False((await presets.GetSetupStatusAsync()).IsComplete);
    }

    private sealed class TestApplication : IAsyncDisposable
    {
        private readonly string _dataRoot;
        private readonly ServiceProvider _provider;

        private TestApplication(string dataRoot, ServiceProvider provider)
        {
            _dataRoot = dataRoot;
            _provider = provider;
        }

        public IServiceProvider Services => _provider;

        public static async Task<TestApplication> CreateAsync(TimeProvider? timeProvider = null)
        {
            string dataRoot = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);
            ServiceCollection services = new();
            services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(dataRoot));
            services.AddSingleton(new DebugResetAvailability(true));
            if (timeProvider is not null)
            {
                services.AddSingleton(timeProvider);
            }
            services.AddDnaXHosting(options => options.WritableDataRoot = dataRoot);
            services.AddMonkeysphereData();
            ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
            await provider.MigrateDnaXDatabaseAsync(MonkeysphereDataExtensions.DatabaseName);
            return new TestApplication(dataRoot, provider);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_dataRoot))
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
        }

        private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
        {
            public string EnvironmentName { get; set; } = Environments.Development;
            public string ApplicationName { get; set; } = "Monkeysphere.Data.Tests";
            public string ContentRootPath { get; set; } = contentRoot;
            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
