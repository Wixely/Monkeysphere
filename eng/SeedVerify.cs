#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false
#:property InvariantGlobalization=false
#:project ../src/Monkeysphere.Data/Monkeysphere.Data.csproj

// Seeds a data root with enough to look at: the Person starter pack so the dashboard has several
// categories, people whose birthdays fall near today so the calendar and its reminders have something
// to show, a one-off date alongside a repeating one, and a relationship so the graph has an edge.
//
//     dotnet run SeedVerify.cs -- --data-root <path>

using System.Globalization;
using DnaX.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monkeysphere.Core;
using Monkeysphere.Data;

string dataRoot = args.SkipWhile(item => item != "--data-root").Skip(1).FirstOrDefault()
    ?? throw new InvalidOperationException("--data-root is required.");
Console.WriteLine($"Data root: {dataRoot}");

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDnaXHosting(options => options.WritableDataRoot = dataRoot);
builder.Services.AddMonkeysphereData();
using IHost host = builder.Build();
await host.Services.InitializeMonkeysphereDomainsAsync();

await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
IPresetService presets = scope.ServiceProvider.GetRequiredService<IPresetService>();
IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
IRelationshipService relationships = scope.ServiceProvider.GetRequiredService<IRelationshipService>();
IReminderService reminders = scope.ServiceProvider.GetRequiredService<IReminderService>();
IDashboardService dashboard = scope.ServiceProvider.GetRequiredService<IDashboardService>();

foreach (string key in (string[])["monkeysphere.person", "monkeysphere.workplace", "monkeysphere.home", "monkeysphere.event"])
{
    try
    {
        await presets.InstallPresetAsync(key);
        Console.WriteLine($"Installed {key}");
    }
    catch (DomainValidationException exception)
    {
        Console.WriteLine($"Skipped {key}: {exception.Message}");
    }
}

IReadOnlyList<RecordType> types = await records.ListRecordTypesAsync();
RecordType person = types.Single(type => type.PresetKey == "monkeysphere.person");
RecordTypeDetails personDetails = (await records.GetRecordTypeAsync(person.Id))!;
Guid birthday = personDetails.Fields.Single(field => field.Definition.Name == "Birthday").Definition.Id;

// A one-off date as well, so the calendar and the reminder list show both kinds side by side.
FieldDefinition joined = await records.CreateAndAttachFieldAsync(person.Id, new("Joined", FieldTypes.ExactDate, false));

DateOnly today = DateOnly.FromDateTime(DateTime.Now);
(string Name, int DaysAway, int Year)[] people =
[
    ("Ada Lovelace", 3, 1815),
    ("Grace Hopper", 10, 1906),
    ("Alan Turing", 28, 1912),
    ("Katherine Johnson", 120, 1918),
];

List<RecordDetails> created = [];
foreach ((string name, int daysAway, int year) in people)
{
    DateOnly occurrence = today.AddDays(daysAway);
    DateOnly born = new(year, occurrence.Month, occurrence.Day);
    created.Add(await records.CreateRecordAsync(
        person.Id,
        name,
        [Day(birthday, born)],
        null,
        ["seed"]));
    Console.WriteLine($"{name}: born {born:yyyy-MM-dd}, next occurrence {occurrence:yyyy-MM-dd}");
}

RecordDetails oneOff = await records.CreateRecordAsync(
    person.Id, "Jean Bartik", [new(joined.Id, "2020-03-04")], null, ["seed"]);

// Reminders on both: a repeating date with a week's warning and a month's, and the one-off.
Guid adaValue = created[0].Values.Single(value => value.FieldDefinitionId == birthday).Id;
Guid graceValue = created[1].Values.Single(value => value.FieldDefinitionId == birthday).Id;
Guid joinedValue = oneOff.Values.Single(value => value.FieldDefinitionId == joined.Id).Id;
foreach ((Guid valueId, int lead) in ((Guid, int)[])[(adaValue, 7), (adaValue, 30), (graceValue, 1), (joinedValue, 0)])
{
    _ = await reminders.CreateAsync(valueId, lead);
}

RelationshipType knows = await relationships.CreateTypeAsync(new("knows", RelationshipDirectionality.Directional, "known by"));
_ = await relationships.CreateAsync(knows.Id, created[0].Record.Id, created[1].Record.Id);
_ = await relationships.CreateAsync(knows.Id, created[1].Record.Id, created[2].Record.Id);

IReadOnlyList<ReminderItem> active = await reminders.ListActiveAsync();
Console.WriteLine();
Console.WriteLine($"Active reminders: {active.Count}");
foreach (ReminderItem item in active)
{
    Console.WriteLine($"  due {item.DueDate:yyyy-MM-dd}  occurrence {item.Entry.Date:yyyy-MM-dd}  stored {item.StoredDate:yyyy-MM-dd}  lead {item.Reminder.LeadDays}  {item.Entry.RecordDisplayName}");
}

DashboardConfiguration configuration = await dashboard.GetConfigurationAsync();
Console.WriteLine();
Console.WriteLine($"Dashboard categories: {configuration.RecordTypeIds.Count} of {types.Count(type => type.Lifecycle == RecordTypeLifecycle.Active)} active types");
Console.WriteLine($"Look-ahead: {configuration.UpcomingDays} days");

static FieldValueInput Day(Guid fieldId, DateOnly date) => new(
    fieldId,
    Temporal: new TemporalValueInput(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), TemporalPrecision.Day, false, null));
