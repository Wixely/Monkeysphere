using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// A hidden record must be unobservable from every ordinary read. This enumerates the read
/// surfaces rather than trusting that each one remembered the filter: the whole point of the
/// feature is that forgetting a single path leaks the record, so the test is the safety net.
/// Hiding is applied with direct SQL so this exercises the filter, not the setter.
///
/// The same enumeration drives both tests. An ordinary reader must observe the record through
/// none of these surfaces, a backstage reader through every one of them, which is what stops a
/// surface from passing the leak test only because the fixture never reached it.
/// </summary>
public sealed class BackstageLeakTests
{
    private static readonly string[] Surfaces =
    [
        "GetRecordAsync by id",
        "SearchRecordsAsync listing",
        "SearchRecordsAsync by name",
        "SearchRecordsAsync by alias",
        "SearchRecordsAsync by field value",
        "IRelationshipService.ListForRecordAsync",
        "IRelationshipService.QueryForRecordAsync",
        "relationship graph nodes",
        "relationship graph edges",
        "spatial map",
        "calendar",
        "dashboard upcoming dates",
        "active reminders",
        "field conversion preview",
        "vCard duplicate discovery",
        "vCard export",
        "retained source material",
    ];

    private sealed record Fixture(
        Guid HiddenId, Guid VisibleId,
        Guid TypeId, Guid DateFieldId, Guid TextFieldId, Guid LocationFieldId);

    private static async Task<Fixture> SeedAsync(IServiceProvider services)
    {
        IMonkeysphereService records = services.GetRequiredService<IMonkeysphereService>();
        // The person preset, so that the vCard surfaces (which only consider that preset) apply.
        await services.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        RecordType type = (await records.ListRecordTypesAsync()).Single(item => item.PresetKey == "monkeysphere.person");
        FieldDefinition text = await records.CreateAndAttachFieldAsync(type.Id, new("Notes", FieldTypes.Text, false));
        FieldDefinition date = await records.CreateAndAttachFieldAsync(type.Id, new("Anniversary", FieldTypes.ExactDate, false));
        FieldDefinition place = await records.CreateAndAttachFieldAsync(type.Id, new("Where", FieldTypes.Location, false));

        // Dated a few days out so it falls inside both the dashboard window and the reminder lead.
        DateOnly anniversary = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(3));
        RecordDetails hidden = await records.CreateRecordAsync(type.Id, "Concealed Cornelius",
        [
            new(text.Id, "a secret note"),
            new(date.Id, anniversary.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new(place.Id, Location: new LocationValueInput("Secret place", "51.5", "-0.12")),
        ], ["Cornelius alias"]);

        RecordDetails visible = await records.CreateRecordAsync(type.Id, "Plainly Visible", []);

        // Retained import material, inserted directly so this exercises the visibility filter
        // rather than the importer. A hidden record's raw source must be as unreachable as the record.
        await using (SqliteConnection connection =
            await services.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                INSERT INTO RecordSourceImports (Id, RecordId, SourceKind, SourceFormat, Fingerprint, ImportedAtUtc)
                    VALUES ('00000000-0000-4000-8000-0000000000b5', @Id, 'vcard', '4.0', 'leak-fixture', '2026-09-10T00:00:00Z');
                INSERT INTO RecordSourceValues (RecordId, Ordinal, ImportId, Name, ParametersJson, RawValue, Mapping)
                    VALUES (@Id, 0, '00000000-0000-4000-8000-0000000000b5', 'X-SECRET', '[]', 'a concealed extension', 0);
                """, new { Id = hidden.Record.Id.ToString("D") });
        }

        Guid dateValueId = hidden.Values.Single(value => value.FieldDefinitionId == date.Id).Id;
        await services.GetRequiredService<IReminderService>().CreateAsync(dateValueId, 7);

        IRelationshipService relationships = services.GetRequiredService<IRelationshipService>();
        RelationshipType link = await relationships.CreateTypeAsync(new("Knows", RelationshipDirectionality.Symmetric));
        await relationships.CreateAsync(link.Id, visible.Record.Id, hidden.Record.Id);

        return new(hidden.Record.Id, visible.Record.Id, type.Id, date.Id, text.Id, place.Id);
    }

    private static async Task HideAsync(IServiceProvider services, Guid recordId)
    {
        await using SqliteConnection connection =
            await services.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        int changed = await connection.ExecuteAsync(
            "UPDATE Records SET BackstageState = 'hidden' WHERE Id = @Id;",
            new { Id = recordId.ToString("D") });
        Assert.Equal(1, changed);
    }

    /// <summary>Names every surface through which the hidden record is observable to this reader.</summary>
    private static async Task<List<string>> ObservableThroughAsync(IServiceProvider services, Fixture fixture)
    {
        List<string> observed = [];
        void Seen(string surface) => observed.Add(surface);

        IMonkeysphereService records = services.GetRequiredService<IMonkeysphereService>();

        if (await records.GetRecordAsync(fixture.HiddenId) is not null) Seen("GetRecordAsync by id");

        PagedResult<RecordSummary> all = await records.SearchRecordsAsync(new());
        if (all.Items.Any(item => item.Id == fixture.HiddenId)) Seen("SearchRecordsAsync listing");
        if ((await records.SearchRecordsAsync(new("Concealed"))).Items.Count != 0) Seen("SearchRecordsAsync by name");
        if ((await records.SearchRecordsAsync(new("Cornelius alias"))).Items.Count != 0) Seen("SearchRecordsAsync by alias");
        if ((await records.SearchRecordsAsync(new("a secret note"))).Items.Count != 0) Seen("SearchRecordsAsync by field value");

        // Read from the visible record's perspective: the link itself is relational metadata.
        IRelationshipService relationships = services.GetRequiredService<IRelationshipService>();
        if ((await relationships.ListForRecordAsync(fixture.VisibleId)).Count != 0) Seen("IRelationshipService.ListForRecordAsync");
        if ((await relationships.QueryForRecordAsync(fixture.VisibleId)).TotalCount != 0) Seen("IRelationshipService.QueryForRecordAsync");

        RelationshipGraphResult graph = await services.GetRequiredService<IRelationshipGraphService>().QueryAsync(new());
        if (graph.Nodes.Any(node => node.RecordId == fixture.HiddenId)) Seen("relationship graph nodes");
        if (graph.Edges.Count != 0) Seen("relationship graph edges");

        PagedResult<SpatialMapEntry> map = await services.GetRequiredService<ISpatialMapService>()
            .QueryAsync(new(RecordTypeId: fixture.TypeId, FieldDefinitionId: fixture.LocationFieldId));
        if (map.Items.Any(item => item.RecordId == fixture.HiddenId)) Seen("spatial map");

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        IReadOnlyList<CalendarEntry> calendar = await services.GetRequiredService<ICalendarService>()
            .QueryAsync(new(today, today.AddDays(30)));
        if (calendar.Any(entry => entry.RecordId == fixture.HiddenId)) Seen("calendar");

        IReadOnlyList<DashboardUpcomingDate> upcoming = await services.GetRequiredService<IDashboardService>()
            .ListUpcomingAsync(new DashboardConfiguration([fixture.TypeId], [fixture.DateFieldId], 30));
        if (upcoming.Any(entry => entry.Source.RecordId == fixture.HiddenId)) Seen("dashboard upcoming dates");

        IReadOnlyList<ReminderItem> reminders = await services.GetRequiredService<IReminderService>().ListActiveAsync();
        if (reminders.Any(item => item.Reminder.RecordId == fixture.HiddenId)) Seen("active reminders");

        // A conversion preview names the record behind every value it could not convert. The
        // conversion itself still rewrites hidden values; only the name is withheld.
        FieldConversionPreview conversion = await records.PreviewFieldConversionAsync(
            fixture.TextFieldId, new("Notes", FieldTypes.Number));
        Assert.Equal(1, conversion.FailedValueCount);
        if (conversion.Issues.Any(issue => issue.RecordId == fixture.HiddenId)) Seen("field conversion preview");

        // Contact duplicate discovery would disclose the name of a hidden person.
        IVCardStore vcards = services.GetRequiredService<IVCardStore>();
        if ((await vcards.ListExistingAsync(fixture.TypeId)).Any(existing => existing.RecordId == fixture.HiddenId))
            Seen("vCard duplicate discovery");

        if ((await vcards.ReadExportAsync([fixture.HiddenId])).Count != 0) Seen("vCard export");

        if (await services.GetRequiredService<IRecordSourceService>().GetAsync(fixture.HiddenId) is not null)
            Seen("retained source material");

        return observed;
    }

    [Fact]
    public async Task AHiddenRecordIsInvisibleToEveryOrdinaryRead()
    {
        await using TestApplication host = await TestApplication.CreateAsync();
        Fixture fixture = await SeedAsync(host.Services);
        await HideAsync(host.Services, fixture.HiddenId);

        List<string> observed = await ObservableThroughAsync(host.Services, fixture);
        Assert.True(observed.Count == 0,
            "A hidden record leaked through:" + Environment.NewLine + string.Join(Environment.NewLine, observed));

        // The record is withheld, not merely filtered out of the page: the count must agree.
        Assert.Equal(1, (await host.Services.GetRequiredService<IMonkeysphereService>().SearchRecordsAsync(new())).TotalCount);
    }

    [Fact]
    public async Task ABackstageReaderSeesTheHiddenRecordEverywhere()
    {
        await using TestApplication host = await TestApplication.CreateAsync(backstage: true);
        Fixture fixture = await SeedAsync(host.Services);
        await HideAsync(host.Services, fixture.HiddenId);

        List<string> observed = await ObservableThroughAsync(host.Services, fixture);
        string[] missing = Surfaces.Except(observed, StringComparer.Ordinal).ToArray();
        Assert.True(missing.Length == 0,
            "The fixture never reached these surfaces, so the leak test proves nothing about them:"
            + Environment.NewLine + string.Join(Environment.NewLine, missing));

        Assert.Equal(2, (await host.Services.GetRequiredService<IMonkeysphereService>().SearchRecordsAsync(new())).TotalCount);
    }
}
