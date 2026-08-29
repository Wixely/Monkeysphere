using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using DnaX.Data.Migrations;
using DnaX.Hosting;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;
using Monkeysphere.Data;

namespace Monkeysphere.Data.Tests;

public sealed class RelationshipWorkflowTests
{
    [Fact]
    public async Task DirectionalRelationshipUsesForwardAndInverseLabels()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        RecordDetails parent = await records.CreateRecordAsync(person.Id, "Parent", []);
        RecordDetails child = await records.CreateRecordAsync(person.Id, "Child", []);
        RelationshipType parentOf = await relationships.CreateTypeAsync(new(
            "parent of", RelationshipDirectionality.Directional, "child of"));

        RelationshipView created = await relationships.CreateAsync(parentOf.Id, parent.Record.Id, child.Record.Id, "confirmed");
        RelationshipView fromParent = Assert.Single(await relationships.ListForRecordAsync(parent.Record.Id));
        RelationshipView fromChild = Assert.Single(await relationships.ListForRecordAsync(child.Record.Id));

        Assert.Equal(created.Id, fromParent.Id);
        Assert.Equal("parent of", fromParent.Label);
        Assert.Equal(child.Record.Id, fromParent.RelatedRecordId);
        Assert.True(fromParent.IsOutgoing);
        Assert.Equal("child of", fromChild.Label);
        Assert.Equal(parent.Record.Id, fromChild.RelatedRecordId);
        Assert.False(fromChild.IsOutgoing);
        Assert.Equal("confirmed", fromChild.Note);
    }

    [Fact]
    public async Task SymmetricRelationshipsCanonicalizeDuplicatesAndCascadeWithRecords()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        RecordType person = await records.CreateRecordTypeAsync("Person");
        RecordDetails first = await records.CreateRecordAsync(person.Id, "First", []);
        RecordDetails second = await records.CreateRecordAsync(person.Id, "Second", []);
        RelationshipType sibling = await relationships.CreateTypeAsync(new(
            "sibling of", RelationshipDirectionality.Symmetric));

        await relationships.CreateAsync(sibling.Id, first.Record.Id, second.Record.Id);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            relationships.CreateAsync(sibling.Id, second.Record.Id, first.Record.Id));
        Assert.Equal("sibling of", Assert.Single(await relationships.ListForRecordAsync(first.Record.Id)).Label);

        Assert.True(await records.DeleteRecordAsync(second.Record.Id));
        Assert.Empty(await relationships.ListForRecordAsync(first.Record.Id));
    }

    [Fact]
    public async Task RelationshipTypeLifecycleAndValidationFailClosed()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        await Assert.ThrowsAsync<DomainValidationException>(() => relationships.CreateTypeAsync(new(
            "parent of", RelationshipDirectionality.Directional)));
        RelationshipType type = await relationships.CreateTypeAsync(new(
            "knows", RelationshipDirectionality.Symmetric, "ignored"));
        Assert.Null(type.InverseName);
        await relationships.RenameTypeAsync(type.Id, "is acquainted with", null);
        await relationships.RetireTypeAsync(type.Id);
        RelationshipType retired = Assert.Single(await relationships.ListTypesAsync());
        Assert.Equal(RelationshipLifecycle.Retired, retired.Lifecycle);
    }
}

internal sealed class TestApplication : IAsyncDisposable
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
