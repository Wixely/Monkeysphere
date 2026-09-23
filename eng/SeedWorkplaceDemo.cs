#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false
#:property InvariantGlobalization=false
#:project ../src/Monkeysphere.Data/Monkeysphere.Data.csproj

// Adds a workplace and a "works at" relationship type to a local data root, so the graph's bulk
// relationship assignment has something real to be pointed at.
//
//     dotnet run eng/SeedWorkplaceDemo.cs -- --data-root .local/some-root

using System.Runtime.CompilerServices;
using DnaX.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monkeysphere.Core;
using Monkeysphere.Data;

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

IReadOnlyList<RecordType> types = await records.ListRecordTypesAsync();
RecordType workplace = types.FirstOrDefault(type => type.Name == "Workplace")
    ?? await records.CreateRecordTypeAsync("Workplace", "🏢");

if (!(await records.SearchRecordsAsync(new("Acme"))).Items.Any())
{
    _ = await records.CreateRecordAsync(workplace.Id, "Acme Engineering", []);
    Console.WriteLine("Created Acme Engineering.");
}

if (!(await relationships.ListTypesAsync()).Any(type => type.Name == "works at"))
{
    _ = await relationships.CreateTypeAsync(new("works at", RelationshipDirectionality.Directional, "employs"));
    Console.WriteLine("Created the works at relationship type.");
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
