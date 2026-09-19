#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false
#:property InvariantGlobalization=false
#:project ../src/Monkeysphere.Data/Monkeysphere.Data.csproj

// Seeds a handful of tagged records into a local data root so the universal-tag UI has something
// to show: shared tags across records, so suggestions and search are worth looking at.
//
// It goes through the ordinary application services rather than writing SQL, so everything it
// creates obeys the same validation the browser does, and a schema older than migration 32 fails
// here rather than producing rows the application would not accept.
//
// Run it from the repository root with the VS Code data root already migrated:
//
//     dotnet run eng/SeedTagDemo.cs
//     dotnet run eng/SeedTagDemo.cs -- --data-root .local/some-other-root
//
// Safe to run twice: a record whose display name is already present is left alone.

using System.Runtime.CompilerServices;
using DnaX.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monkeysphere.Core;
using Monkeysphere.Data;

string dataRoot = ResolveDataRoot(args);
Console.WriteLine($"Data root: {dataRoot}");
Directory.CreateDirectory(dataRoot);

// The ordinary host builder rather than a hand-built provider, so scope validation and the
// environment come from the same place the application gets them.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDnaXHosting(options => options.WritableDataRoot = dataRoot);
builder.Services.AddMonkeysphereData();

using IHost host = builder.Build();

// Creates the databases if they are absent and brings them to the current schema, so this works
// against a fresh root as well as one the application has already opened.
await host.Services.InitializeMonkeysphereDomainsAsync();

await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
IPresetService presets = scope.ServiceProvider.GetRequiredService<IPresetService>();

// The Person preset gives the demo a real record type with real fields. Installing it also
// completes first-run setup, so the browser opens on the application rather than the wizard.
IReadOnlySet<string> installed = await presets.ListInstalledPresetKeysAsync();
if (!installed.Contains("monkeysphere.person"))
{
    Console.WriteLine("Installing the Person preset.");
    await presets.InstallPresetAsync("monkeysphere.person");
}

RecordType person = (await records.ListRecordTypesAsync())
    .First(type => type.PresetKey == "monkeysphere.person");

if (!person.TagsEnabled)
{
    Console.WriteLine("Person has tags turned off; turning them back on for the demo.");
    await records.UpdateRecordTypeAsync(person.Id, person.Name, person.Symbol, tagsEnabled: true);
}

// Tags deliberately overlap, because one record with three tags shows almost nothing: the point of
// the feature is the labels that recur across records. "  Work  " and "work" appear with different
// spacing and casing so the normalization is visible in the editor after saving.
(string Name, string[] Tags)[] people =
[
    ("Ada Lovelace", ["  Work  ", "mathematician", "london"]),
    ("Charles Babbage", ["work", "Mathematician", "london", "inventor"]),
    ("Grace Hopper", ["work", "navy", "mentor"]),
    ("Mira Patel", ["book club", "neighbour", "london"]),
    ("Tomás Rivera", ["book club", "neighbour"]),
    ("Sunday Five-a-side", ["football", "weekly"]),
];

int created = 0;
foreach ((string name, string[] tags) in people)
{
    PagedResult<RecordSummary> existing = await records.SearchRecordsAsync(new(name, person.Id));
    if (existing.Items.Any(item => string.Equals(item.DisplayName, name, StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine($"  skipped {name} (already present)");
        continue;
    }

    RecordDetails record = await records.CreateRecordAsync(person.Id, name, [], null, tags);
    Console.WriteLine($"  created {name} [{string.Join(", ", record.Tags)}]");
    created++;
}

Console.WriteLine();
Console.WriteLine($"Done. {created} record(s) created.");
Console.WriteLine("Press F5 in VS Code (\"Monkeysphere Web\"), sign in as admin/admin, then:");
Console.WriteLine("  - open any person to see the Tags box, and start typing to get suggestions;");
Console.WriteLine("  - search \"london\" or \"book club\" from Records to match on tags alone;");
Console.WriteLine("  - Structures -> Person has the Tags toggle that removes tags from the type.");

static string ResolveDataRoot(string[] args)
{
    for (int index = 0; index < args.Length - 1; index++)
    {
        if (args[index] is "--data-root" or "-d")
        {
            // Relative to where the person is standing, which is what they mean when they type it.
            return Path.GetFullPath(args[index + 1]);
        }
    }

    // Anchored to this file rather than the working directory. `dotnet run eng/SeedTagDemo.cs`
    // runs with eng/ as the working directory, so resolving the default relatively would quietly
    // seed eng/.local/vscode-data and leave the root F5 actually uses empty.
    string repositoryRoot = Path.GetFullPath(Path.Combine(SourceDirectory(), ".."));
    return Path.Combine(repositoryRoot, ".local", "vscode-data");
}

static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
