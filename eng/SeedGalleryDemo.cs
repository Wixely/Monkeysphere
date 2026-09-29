#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false
#:property InvariantGlobalization=false
#:project ../src/Monkeysphere.Data/Monkeysphere.Data.csproj

// Seeds a trainspotting collection and a Gallery saved view to look at it through, so the gallery view
// type can be tried against something with the shape it was built for.
//
//     dotnet run eng/SeedGalleryDemo.cs -- --data-root .local/some-root
//
// The pictures are drawn rather than downloaded, in deliberately different sizes and shapes: a collage
// built only from identical squares proves nothing about how it handles a tall photograph next to a
// wide one, which is what a real collection is full of.
//
// Each locomotive gets a different number of pictures — 1, 2, 3, 4, 5 and 9 — because the collage
// arranges itself by how many there are, and the nine is the one that proves a panel says "+4 more"
// instead of becoming nine pictures tall.

using System.Globalization;
using System.Runtime.CompilerServices;
using DnaX.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monkeysphere.Core;
using Monkeysphere.Data;
using SkiaSharp;

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
IRecordImageService images = scope.ServiceProvider.GetRequiredService<IRecordImageService>();
ISavedViewService views = scope.ServiceProvider.GetRequiredService<ISavedViewService>();

RecordType locomotive = await TypeAsync("Locomotive", "🚆");
RecordType depot = await TypeAsync("Depot", "🏭");
RecordType livery = await TypeAsync("Livery", "🎨");

FieldDefinition number = await FieldAsync(locomotive.Id, "Running number", FieldTypes.Text);
FieldDefinition classField = await FieldAsync(locomotive.Id, "Class", FieldTypes.Text);
FieldDefinition builtField = await FieldAsync(locomotive.Id, "Built", FieldTypes.Text);
FieldDefinition notes = await FieldAsync(locomotive.Id, "Notes", FieldTypes.Text);

RelationshipType allocatedTo = await RelationshipAsync("allocated to", RelationshipDirectionality.Directional, "hosts");
RelationshipType carries = await RelationshipAsync("carries livery", RelationshipDirectionality.Directional, "worn by");

Console.WriteLine();
Console.WriteLine("Depots and liveries, so a locomotive has something photographed to be connected to.");
RecordDetails doncaster = await EnsureAsync(depot.Id, "Doncaster Works", [], pictures: 2, seed: 11);
RecordDetails crewe = await EnsureAsync(depot.Id, "Crewe Diesel Depot", [], pictures: 1, seed: 21);
RecordDetails eastleigh = await EnsureAsync(depot.Id, "Eastleigh Works", [], pictures: 0, seed: 0);

RecordDetails appleGreen = await EnsureAsync(livery.Id, "LNER Apple Green", [], pictures: 1, seed: 31);
RecordDetails brBlue = await EnsureAsync(livery.Id, "BR Corporate Blue", [], pictures: 1, seed: 41);
RecordDetails intercity = await EnsureAsync(livery.Id, "InterCity Swallow", [], pictures: 1, seed: 51);

Console.WriteLine();
Console.WriteLine("Locomotives, each with a different number of pictures so every collage layout appears.");

// One picture: the collage is that picture, filling the panel.
RecordDetails scotsman = await EnsureAsync(locomotive.Id, "Flying Scotsman",
    [new(number.Id, "60103"), new(classField.Id, "LNER A3"), new(builtField.Id, "1923"),
     new(notes.Id, "Preserved. First locomotive officially recorded at 100 mph.")],
    ["preserved", "steam"], pictures: 1, seed: 101);

// Two: the panel splits down the middle.
RecordDetails mallard = await EnsureAsync(locomotive.Id, "Mallard",
    [new(number.Id, "4468"), new(classField.Id, "LNER A4"), new(builtField.Id, "1938"),
     new(notes.Id, "Holds the world speed record for steam traction.")],
    ["preserved", "steam"], pictures: 2, seed: 201);

// Three and four: a lead picture with a column stacked beside it.
RecordDetails deltic = await EnsureAsync(locomotive.Id, "Royal Scots Grey",
    [new(number.Id, "55022"), new(classField.Id, "BR Class 55"), new(builtField.Id, "1961")],
    ["preserved", "diesel"], pictures: 3, seed: 301);
RecordDetails duff = await EnsureAsync(locomotive.Id, "Western Champion",
    [new(number.Id, "D1015"), new(classField.Id, "BR Class 52"), new(builtField.Id, "1963")],
    ["preserved", "diesel"], pictures: 4, seed: 401);

// Five: a lead picture with a two-by-two beside it, which is the collage at full stretch.
RecordDetails duke = await EnsureAsync(locomotive.Id, "Duke of Gloucester",
    [new(number.Id, "71000"), new(classField.Id, "BR 8P"), new(builtField.Id, "1954")],
    ["preserved", "steam"], pictures: 5, seed: 501);

// Nine: more than a collage draws, so the panel has to say so rather than grow.
RecordDetails tornado = await EnsureAsync(locomotive.Id, "Tornado",
    [new(number.Id, "60163"), new(classField.Id, "LNER A1"), new(builtField.Id, "2008"),
     new(notes.Id, "Built new. Photographed more than any other engine in this collection.")],
    ["mainline", "steam"], pictures: 9, seed: 601);

// And one with no pictures at all, because a gallery has to be honest about that rather than
// collapsing into a broken panel.
RecordDetails unphotographed = await EnsureAsync(locomotive.Id, "Blue Peter",
    [new(number.Id, "60532"), new(classField.Id, "LNER A2")], ["stored", "steam"], pictures: 0, seed: 0);

Console.WriteLine();
Console.WriteLine("Connections, so each panel has pictures to link out to.");
await RelateAsync(allocatedTo, scotsman, doncaster);
await RelateAsync(carries, scotsman, appleGreen);
await RelateAsync(allocatedTo, mallard, doncaster);
await RelateAsync(carries, mallard, appleGreen);
await RelateAsync(allocatedTo, deltic, crewe);
await RelateAsync(carries, deltic, brBlue);
await RelateAsync(allocatedTo, duff, crewe);
await RelateAsync(carries, duff, brBlue);
await RelateAsync(allocatedTo, duke, eastleigh);
await RelateAsync(allocatedTo, tornado, doncaster);
await RelateAsync(carries, tornado, appleGreen);
await RelateAsync(carries, tornado, intercity);
await RelateAsync(allocatedTo, unphotographed, eastleigh);

// A connection that has ended, so the faded "over" state is visible without editing anything by hand.
await EndRelationshipAsync(tornado, intercity);

Console.WriteLine();
await ViewAsync("Locomotives — gallery", SavedViewKind.Gallery, [number.Id, classField.Id, builtField.Id]);

// The same selection as a grid, so the two can be compared side by side and it is obvious that only
// the drawing differs.
await ViewAsync("Locomotives — grid", SavedViewKind.Grid, [number.Id, classField.Id, builtField.Id]);

// And one grouped, because grouping is meant to work identically in both.
await ViewAsync("Locomotives by class — gallery", SavedViewKind.Gallery, [number.Id, builtField.Id], groupBy: classField.Id);

Console.WriteLine();
Console.WriteLine("Seeded. Open /records, choose \"Locomotives — gallery\" and apply it.");

async Task<RecordType> TypeAsync(string name, string symbol) =>
    (await records.ListRecordTypesAsync()).FirstOrDefault(type => type.Name == name)
        ?? await records.CreateRecordTypeAsync(name, symbol);

async Task<FieldDefinition> FieldAsync(Guid typeId, string name, string fieldTypeId)
{
    RecordTypeDetails details = await records.GetRecordTypeAsync(typeId)
        ?? throw new InvalidOperationException($"Record type {typeId} disappeared.");
    RecordTypeField? existing = details.Fields.FirstOrDefault(field =>
        string.Equals(field.Definition.Name, name, StringComparison.OrdinalIgnoreCase));
    return existing?.Definition ?? await records.CreateAndAttachFieldAsync(typeId, new(name, fieldTypeId, false));
}

async Task<RelationshipType> RelationshipAsync(string name, RelationshipDirectionality directionality, string? inverse)
{
    RelationshipType? existing = (await relationships.ListTypesAsync())
        .FirstOrDefault(type => string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase));
    return existing ?? await relationships.CreateTypeAsync(new(name, directionality, inverse));
}

async Task<RecordDetails> EnsureAsync(Guid typeId, string displayName, IReadOnlyList<FieldValueInput> values,
    IReadOnlyList<string>? tags = null, int pictures = 0, int seed = 0)
{
    RecordSummary? found = (await records.SearchRecordsAsync(new(displayName, typeId))).Items
        .FirstOrDefault(item => string.Equals(item.DisplayName, displayName, StringComparison.Ordinal));
    RecordDetails record;
    if (found is not null)
    {
        Console.WriteLine($"  {displayName} already there.");
        record = await records.GetRecordAsync(found.Id)
            ?? throw new InvalidOperationException($"Record {displayName} disappeared.");
    }
    else
    {
        record = await records.CreateRecordAsync(typeId, displayName, values, tags: tags);
        Console.WriteLine($"  Created {displayName} with {pictures} picture(s).");
    }

    if (record.Images.Count > 0 || pictures == 0) return record;

    for (int index = 0; index < pictures; index++)
    {
        using MemoryStream content = new(Picture(displayName, index, seed + index));
        RecordImage added = await images.AddAsync(record.Record.Id, content, $"{Slug(displayName)}-{index + 1}.png");

        // A caption on some but not all, so the collage's caption overlay can be seen doing both.
        if (index % 2 == 0)
        {
            await images.UpdateMetadataAsync(record.Record.Id, added.Id,
                $"{displayName} · view {index + 1}", isCover: index == 0);
        }
    }

    return await records.GetRecordAsync(record.Record.Id) ?? record;
}

async Task RelateAsync(RelationshipType type, RecordDetails from, RecordDetails to)
{
    try
    {
        _ = await relationships.CreateAsync(type.Id, from.Record.Id, to.Record.Id);
        Console.WriteLine($"  {from.Record.DisplayName} {type.Name} {to.Record.DisplayName}.");
    }
    catch (DomainValidationException)
    {
        // Already related, which is what a second run should be.
    }
}

async Task EndRelationshipAsync(RecordDetails from, RecordDetails to)
{
    RelationshipView? link = (await relationships.ListForRecordAsync(from.Record.Id))
        .FirstOrDefault(view => view.RelatedRecordId == to.Record.Id);
    if (link is null || link.Expiry.Expired) return;
    await relationships.UpdateAsync(link.Id, link.RelationshipTypeId, link.Note, from.Record.Id,
        expectedRevision: link.Revision, expiry: new RelationshipExpiry(Expired: true));
    Console.WriteLine($"  Marked {from.Record.DisplayName} → {to.Record.DisplayName} as over.");
}

async Task ViewAsync(string name, SavedViewKind kind, IReadOnlyList<Guid> columns, Guid? groupBy = null)
{
    SavedView? existing = (await views.ListAsync())
        .FirstOrDefault(view => string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase));
    SaveViewRequest request = new(
        name,
        locomotive.Id,
        Query: null,
        ColumnFieldDefinitionIds: columns,
        Filters: [],
        GroupByFieldDefinitionId: groupBy,
        SortFieldDefinitionId: null,
        SortDescending: false,
        Tags: null,
        ShowTags: true,
        Kind: kind);
    if (existing is null)
    {
        await views.CreateAsync(request);
        Console.WriteLine($"  Created the \"{name}\" view.");
    }
    else
    {
        await views.UpdateAsync(existing.Id, request);
        Console.WriteLine($"  Updated the \"{name}\" view.");
    }
}

/// <summary>
/// A drawn stand-in for a photograph: a banded sky, a horizon, a rail and a coloured body with the
/// record's name on it. Deliberately varied in size and aspect so the collage is exercised by tall and
/// wide pictures rather than by a set of identical squares.
/// </summary>
static byte[] Picture(string title, int index, int seed)
{
    (int width, int height) = (seed % 3) switch
    {
        0 => (1200, 800),
        1 => (800, 1100),
        _ => (1400, 620),
    };

    SKColor body = SKColor.FromHsl((seed * 47) % 360, 62, 46);
    SKColor sky = SKColor.FromHsl((200 + (seed * 11)) % 360, 38, 78);

    using SKBitmap bitmap = new(width, height);
    using SKCanvas canvas = new(bitmap);
    canvas.Clear(sky);

    int horizon = (int)(height * 0.62);
    using (SKPaint ground = new() { Color = SKColor.FromHsl(92, 22, 34) })
    {
        canvas.DrawRect(0, horizon, width, height - horizon, ground);
    }

    using (SKPaint ballast = new() { Color = SKColor.FromHsl(30, 10, 52) })
    {
        canvas.DrawRect(0, horizon - (height / 14f), width, height / 7f, ballast);
    }

    // The locomotive: a body, a cab lighter than it, and a wheel or two under it.
    float bodyHeight = height * 0.26f;
    float bodyTop = horizon - bodyHeight;
    using (SKPaint paint = new() { Color = body })
    {
        canvas.DrawRoundRect(width * 0.08f, bodyTop, width * 0.8f, bodyHeight, 10, 10, paint);
    }

    using (SKPaint cab = new() { Color = body.WithAlpha(190) })
    {
        canvas.DrawRoundRect(width * 0.62f, bodyTop - (bodyHeight * 0.42f), width * 0.24f, bodyHeight * 0.5f, 6, 6, cab);
    }

    using (SKPaint wheel = new() { Color = SKColors.Black.WithAlpha(210) })
    {
        for (int axle = 0; axle < 3; axle++)
        {
            canvas.DrawCircle(width * (0.2f + (axle * 0.22f)), horizon, bodyHeight * 0.22f, wheel);
        }
    }

    using SKFont font = new(SKTypeface.Default, height * 0.055f);
    using SKPaint text = new() { Color = SKColors.White.WithAlpha(230) };
    canvas.DrawText(
        string.Create(CultureInfo.InvariantCulture, $"{title} · {index + 1}"),
        width * 0.1f, bodyTop + (bodyHeight * 0.62f), SKTextAlign.Left, font, text);

    using SKImage image = SKImage.FromBitmap(bitmap);
    using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 90);
    return encoded.ToArray();
}

static string Slug(string value) =>
    string.Concat(value.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-'));

static string ResolveDataRoot(string[] args)
{
    for (int index = 0; index < args.Length - 1; index++)
    {
        if (args[index] is "--data-root" or "-d") return Path.GetFullPath(args[index + 1]);
    }

    return Path.Combine(Path.GetFullPath(Path.Combine(SourceDirectory(), "..")), ".local", "vscode-data");
}

static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
