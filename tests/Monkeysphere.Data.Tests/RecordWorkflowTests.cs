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
        ]);

        RecordDetails reloaded = Assert.IsType<RecordDetails>(await service.GetRecordAsync(created.Record.Id));
        Assert.Equal("42.50", Assert.Single(reloaded.Values, item => item.FieldDefinitionId == score.Id).NumberValue);
        Assert.Equal("Friend", Assert.Single(reloaded.Values, item => item.FieldDefinitionId == category.Id).TextValue);
        Assert.Equal(["mathematics", "Pioneer"], Assert.Single(reloaded.Values, item => item.FieldDefinitionId == tags.Id).Tags);
        Assert.Equal("First programmer", Assert.Single(reloaded.Values, item => item.FieldDefinitionId == custom.Id).TextValue);

        PagedResult<RecordSummary> textSearch = await service.SearchRecordsAsync(new RecordSearch(Query: "pioneer"));
        Assert.Equal(created.Record.Id, Assert.Single(textSearch.Items).Id);

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
        ]);
        Assert.Equal("Augusta Ada King", updated.Record.DisplayName);
        Assert.DoesNotContain(updated.Values, item => item.FieldDefinitionId == score.Id);

        Assert.True(await service.DeleteRecordAsync(created.Record.Id));
        Assert.Null(await service.GetRecordAsync(created.Record.Id));
        Assert.False(await service.DeleteRecordAsync(created.Record.Id));
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
        RecordType person = await service.CreateRecordTypeAsync("Person");
        RecordType pet = await service.CreateRecordTypeAsync("Pet");

        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateRecordTypeAsync("person"));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.RenameRecordTypeAsync(pet.Id, person.Name));
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

        public static async Task<TestApplication> CreateAsync()
        {
            string dataRoot = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);
            ServiceCollection services = new();
            services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(dataRoot));
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
}
