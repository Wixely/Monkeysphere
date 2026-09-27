#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false
#:property InvariantGlobalization=false
#:project ../src/Monkeysphere.Data/Monkeysphere.Data.csproj

// Seeds duplicate records to merge, so the Merge a duplicate panel and the merge_records tool can be
// tried against something that actually exercises them.
//
//     dotnet run eng/SeedMergeDemo.cs -- --data-root .local/some-root
//
// Every pair is deliberately awkward in a different way, because a merge of two records that agree
// about everything proves nothing:
//
//   Ada Lovelace / Augusta Ada King -- the ordinary case, and the one with every kind of trouble in it.
//     They disagree about one date of birth and one phone number; each knows something the other does
//     not; their skills lists overlap; both are a parent of the same child, so one of those links has
//     to go; they are recorded as related to each other, which is how somebody usually notices; each
//     has a cover image, and only one record can have one; and both have a reminder on their own
//     birthday at the same lead, so the two cannot both survive.
//
//   Charles Babbage / Babbage & Co -- a Person and a Company. The survivor keeps its own type, so the
//     company's registration number has nowhere to live as record data and must end up readable as
//     source material instead. This is the pair that shows merging across types is not lossy.
//
//   Mary Somerville -- imported twice from two slightly different cards, so each record already carries
//     retained vCard material that the merge has to move rather than strand, including a custom
//     property neither record ever understood.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DnaX.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monkeysphere.Core;
using Monkeysphere.Data;

// A 1x1 PNG. The point is only that two records each have a cover image, not what is in it.
const string PixelBase64 =
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

string dataRoot = ResolveDataRoot(args);
Console.WriteLine($"Data root: {dataRoot}");

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDnaXHosting(options => options.WritableDataRoot = dataRoot);
builder.Services.AddMonkeysphereData();
using IHost host = builder.Build();
await host.Services.InitializeMonkeysphereDomainsAsync();

await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
IRelationshipService relationships = scope.ServiceProvider.GetRequiredService<IRelationshipService>();
IReminderService reminders = scope.ServiceProvider.GetRequiredService<IReminderService>();
IRecordImageService images = scope.ServiceProvider.GetRequiredService<IRecordImageService>();
IVCardService vcards = scope.ServiceProvider.GetRequiredService<IVCardService>();
IPresetService presets = scope.ServiceProvider.GetRequiredService<IPresetService>();

// Contact import refuses to run without it, and the third pair depends on importing. Installing it also
// means the Person the merges are tried on is the real one, with the preset's own fields, rather than a
// look-alike this script invented.
const string PersonPreset = "monkeysphere.person";
if (!(await records.ListRecordTypesAsync()).Any(type => type.PresetKey == PersonPreset))
{
    // Matched on the preset key rather than the name, because contact import matches on the key too: a
    // hand-made record type called Person is not the one it will accept.
    await presets.InstallPresetAsync(PersonPreset);
    Console.WriteLine("Installed the Person preset.");
}

IReadOnlyList<RecordType> types = await records.ListRecordTypesAsync();
RecordType person = types.First(type => type.PresetKey == PersonPreset);
RecordType company = types.FirstOrDefault(type => type.Name == "Company")
    ?? await records.CreateRecordTypeAsync("Company", "🏢");

FieldDefinition birthday = await FieldAsync(person.Id, "Date of birth", FieldTypes.ExactDate);
FieldDefinition phone = await FieldAsync(person.Id, "Phone", FieldTypes.Text);
FieldDefinition notes = await FieldAsync(person.Id, "Notes", FieldTypes.Text);
FieldDefinition skills = await FieldAsync(person.Id, "Skills", FieldTypes.Tags);

// Shared between the two types, so a cross-type merge has somewhere to put at least one value and it
// is visible that the field it cannot place is the only one archived.
FieldDefinition companyPhone = await AttachAsync(company.Id, phone);
FieldDefinition registration = await FieldAsync(company.Id, "Registration number", FieldTypes.Text);

Console.WriteLine();
Console.WriteLine("Pair 1 — two imports of the same person, disagreeing about nearly everything.");
RecordDetails child = await EnsureAsync(person.Id, "Byron King-Noel", []);
RecordDetails mother = await EnsureAsync(person.Id, "Annabella Byron", []);
RecordDetails colleague = await EnsureAsync(person.Id, "Charles Wheatstone", []);

RecordDetails ada = await EnsureAsync(person.Id, "Ada Lovelace",
    [new(birthday.Id, "1815-12-10"), new(notes.Id, "Wrote the first published algorithm."),
     new(skills.Id, Tags: ["mathematics", "notation"])],
    ["Ada Byron"], ["mathematicians"]);
RecordDetails augusta = await EnsureAsync(person.Id, "Augusta Ada King",
    // A different day, a phone number the other record has never had, and an overlapping skills list.
    [new(birthday.Id, "1815-12-11"), new(phone.Id, "+44 20 7946 0100"),
     new(skills.Id, Tags: ["notation", "analytical engines"])],
    ["Countess of Lovelace"], ["peers", "mathematicians"]);

RelationshipType parentOf = await RelationshipAsync("parent of", RelationshipDirectionality.Directional, "child of");
RelationshipType knows = await RelationshipAsync("knows", RelationshipDirectionality.Symmetric);

// Both records claim the same child, so one of the two links is a duplicate the merge has to drop.
await RelateAsync(parentOf, ada.Record.Id, child.Record.Id);
await RelateAsync(parentOf, augusta.Record.Id, child.Record.Id);

// The survivor is someone's child and the duplicate is someone's acquaintance: different relationships
// under different types, both of which must come across.
await RelateAsync(parentOf, mother.Record.Id, ada.Record.Id);
await RelateAsync(knows, augusta.Record.Id, colleague.Record.Id);

// And the two are recorded as knowing each other, which repointing would turn into knowing themselves.
await RelateAsync(knows, ada.Record.Id, augusta.Record.Id);

await CoverAsync(ada, "ada-portrait.png");
await CoverAsync(augusta, "augusta-portrait.png");

// One reminder each, on each record's own birthday, at the same lead. Only one can survive on one value.
await RemindAsync(ada, birthday.Id, 7);
await RemindAsync(augusta, birthday.Id, 7);

Console.WriteLine();
Console.WriteLine("Pair 2 — a person and a company that are the same entity.");
RecordDetails babbage = await EnsureAsync(person.Id, "Charles Babbage",
    [new(notes.Id, "Designed the difference engine.")], tags: ["mathematicians"]);
RecordDetails babbageCo = await EnsureAsync(company.Id, "Babbage & Co",
    [new(companyPhone.Id, "+44 20 7946 0200"), new(registration.Id, "SC000123")]);

Console.WriteLine();
Console.WriteLine("Pair 3 — the same card imported twice, so both records already hold source material.");
await ImportAsync("Mary Somerville", "mary.somerville@example.test", "Queen of Science");
await ImportAsync("Mary Fairfax Somerville", "m.somerville@example.test", "Connexion of the Physical Sciences");

Console.WriteLine();
Console.WriteLine("Seeded. Open a record, scroll to Merge a duplicate, and pick its twin:");
Console.WriteLine($"  Ada Lovelace       {ada.Record.Id}");
Console.WriteLine($"  Augusta Ada King   {augusta.Record.Id}");
Console.WriteLine($"  Charles Babbage    {babbage.Record.Id}");
Console.WriteLine($"  Babbage & Co       {babbageCo.Record.Id}");

async Task<FieldDefinition> FieldAsync(Guid typeId, string name, string fieldTypeId)
{
    RecordTypeDetails details = await records.GetRecordTypeAsync(typeId)
        ?? throw new InvalidOperationException($"Record type {typeId} disappeared.");
    RecordTypeField? existing = details.Fields.FirstOrDefault(field =>
        string.Equals(field.Definition.Name, name, StringComparison.OrdinalIgnoreCase));
    return existing?.Definition ?? await records.CreateAndAttachFieldAsync(typeId, new(name, fieldTypeId, false));
}

async Task<FieldDefinition> AttachAsync(Guid typeId, FieldDefinition field)
{
    RecordTypeDetails details = await records.GetRecordTypeAsync(typeId)
        ?? throw new InvalidOperationException($"Record type {typeId} disappeared.");
    if (details.Fields.All(attached => attached.Definition.Id != field.Id))
    {
        await records.AttachFieldAsync(typeId, field.Id, false);
    }

    return field;
}

async Task<RecordDetails> EnsureAsync(Guid typeId, string displayName, IReadOnlyList<FieldValueInput> values,
    IReadOnlyList<string>? aliases = null, IReadOnlyList<string>? tags = null)
{
    RecordSummary? found = (await records.SearchRecordsAsync(new(displayName, typeId))).Items
        .FirstOrDefault(item => string.Equals(item.DisplayName, displayName, StringComparison.Ordinal));
    if (found is not null)
    {
        Console.WriteLine($"  {displayName} already there.");
        return await records.GetRecordAsync(found.Id)
            ?? throw new InvalidOperationException($"Record {displayName} disappeared.");
    }

    RecordDetails created = await records.CreateRecordAsync(typeId, displayName, values, aliases, tags);
    Console.WriteLine($"  Created {displayName}.");
    return created;
}

async Task<RelationshipType> RelationshipAsync(string name, RelationshipDirectionality directionality, string? inverse = null)
{
    RelationshipType? existing = (await relationships.ListTypesAsync())
        .FirstOrDefault(type => string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase));
    return existing ?? await relationships.CreateTypeAsync(new(name, directionality, inverse));
}

async Task RelateAsync(RelationshipType type, Guid source, Guid target)
{
    try
    {
        _ = await relationships.CreateAsync(type.Id, source, target);
    }
    catch (DomainValidationException)
    {
        // Already related, which is what a second run of the seed should be.
    }
}

async Task CoverAsync(RecordDetails record, string fileName)
{
    if (record.Images.Count > 0) return;
    using MemoryStream content = new(Convert.FromBase64String(PixelBase64));
    RecordImage added = await images.AddAsync(record.Record.Id, content, fileName);

    // Explicitly the cover, because only one record can hold one and the collision is the thing worth
    // trying by hand.
    _ = await images.UpdateMetadataAsync(record.Record.Id, added.Id, "Seeded", isCover: true);
}

async Task RemindAsync(RecordDetails record, Guid fieldDefinitionId, int leadDays)
{
    RecordValue? value = record.Values.FirstOrDefault(item => item.FieldDefinitionId == fieldDefinitionId);
    if (value is null) return;
    try
    {
        _ = await reminders.CreateAsync(value.Id, leadDays);
    }
    catch (DomainValidationException)
    {
        // Already set, which is what a second run of the seed should be.
    }
}

async Task ImportAsync(string fullName, string email, string custom)
{
    if ((await records.SearchRecordsAsync(new(fullName, person.Id))).Items
        .Any(item => string.Equals(item.DisplayName, fullName, StringComparison.Ordinal)))
    {
        Console.WriteLine($"  {fullName} already imported.");
        return;
    }

    byte[] card = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"""
        BEGIN:VCARD
        VERSION:3.0
        FN:{fullName}
        N:{fullName};;;;
        EMAIL;TYPE=home:{email}
        BDAY:1780-12-26
        X-MONKEYSPHERE-EPITHET:{custom}
        END:VCARD
        """));
    VCardImportPreview preview = await vcards.PreviewAsync(card);
    VCardContactPreview contact = preview.Contacts.Single();
    await vcards.ApplyAsync(preview, [new(contact.Index, VCardImportAction.CreateSeparately)]);
    Console.WriteLine($"  Imported {fullName}, carrying X-MONKEYSPHERE-EPITHET as retained source material.");
}

static string ResolveDataRoot(string[] args)
{
    for (int index = 0; index < args.Length - 1; index++)
    {
        if (args[index] is "--data-root" or "-d") return Path.GetFullPath(args[index + 1]);
    }

    return Path.Combine(Path.GetFullPath(Path.Combine(SourceDirectory(), "..")), ".local", "vscode-data");
}

static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
