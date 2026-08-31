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

    [Fact]
    public async Task RelationshipGraphSupportsSearchTypeFilteringAndBoundedNeighbourExpansion()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRecordImageService images = application.Services.GetRequiredService<IRecordImageService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        IRelationshipGraphService graph = application.Services.GetRequiredService<IRelationshipGraphService>();
        RecordType person = await records.CreateRecordTypeAsync("Graph person", "👤");
        RecordDetails ada = await records.CreateRecordAsync(person.Id, "Ada", [], ["Enchantress"]);
        RecordDetails charles = await records.CreateRecordAsync(person.Id, "Charles", []);
        RecordDetails mary = await records.CreateRecordAsync(person.Id, "Mary", []);
        RecordDetails unrelated = await records.CreateRecordAsync(person.Id, "Unrelated", []);
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        RecordImage adaImage = await images.AddAsync(ada.Record.Id, new MemoryStream(png), "ada.png");
        RelationshipType knows = await relationships.CreateTypeAsync(new("knows", RelationshipDirectionality.Symmetric));
        RelationshipType inspired = await relationships.CreateTypeAsync(new("inspired", RelationshipDirectionality.Directional, "inspired by"));
        _ = await relationships.CreateAsync(knows.Id, ada.Record.Id, charles.Record.Id);
        _ = await relationships.CreateAsync(inspired.Id, charles.Record.Id, mary.Record.Id);
        _ = await relationships.CreateAsync(knows.Id, mary.Record.Id, unrelated.Record.Id);

        RelationshipGraphResult search = await graph.QueryAsync(new(Search: "Enchantress"));
        RelationshipGraphNode searchNode = Assert.Single(search.Nodes);
        Assert.Equal(ada.Record.Id, searchNode.RecordId);
        Assert.Equal(adaImage.Id, searchNode.ImageId);
        Assert.Equal("👤", searchNode.RecordTypeSymbol);
        Assert.Empty(search.Edges);

        RelationshipGraphResult depthOne = await graph.QueryAsync(new(FocusRecordId: ada.Record.Id, Depth: 1));
        Assert.Equal([ada.Record.Id, charles.Record.Id], depthOne.Nodes.Select(node => node.RecordId));
        Assert.Null(Assert.Single(depthOne.Nodes, node => node.RecordId == charles.Record.Id).ImageId);
        Assert.Single(depthOne.Edges);

        RelationshipGraphResult filtered = await graph.QueryAsync(new(
            FocusRecordId: ada.Record.Id,
            RelationshipTypeId: knows.Id,
            Depth: 3));
        Assert.Equal([ada.Record.Id, charles.Record.Id], filtered.Nodes.Select(node => node.RecordId));
        Assert.Single(filtered.Edges);

        RelationshipGraphResult truncated = await graph.QueryAsync(new(FocusRecordId: ada.Record.Id, Depth: 3, NodeLimit: 2));
        Assert.True(truncated.NodesTruncated);
        Assert.Equal(2, truncated.Nodes.Count);
        Assert.DoesNotContain(truncated.Nodes, node => node.RecordId == unrelated.Record.Id);
    }

    [Fact]
    public async Task RelationshipGraphEnforcesRenderingBoundsAtAcceptedStorageScale()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        IRelationshipGraphService graph = application.Services.GetRequiredService<IRelationshipGraphService>();
        MonkeysphereConnectionFactory connections = application.Services.GetRequiredService<MonkeysphereConnectionFactory>();
        RecordType type = await records.CreateRecordTypeAsync("Scale person");
        RelationshipType connection = await relationships.CreateTypeAsync(new(
            "scale connection", RelationshipDirectionality.Symmetric));
        Guid focusId = await SeedScaleGraphAsync(connections, type.Id, connection.Id);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        RelationshipGraphResult result = await graph.QueryAsync(new(
            FocusRecordId: focusId,
            Depth: 1,
            NodeLimit: RelationshipGraphService.MaximumNodes,
            EdgeLimit: RelationshipGraphService.MaximumEdges), deadline.Token);

        Assert.Equal(RelationshipGraphService.MaximumNodes, result.Nodes.Count);
        Assert.Equal(RelationshipGraphService.MaximumEdges, result.Edges.Count);
        Assert.True(result.NodesTruncated);
        Assert.True(result.EdgesTruncated);
        Assert.Equal(focusId, result.Nodes[0].RecordId);
    }

    private static async Task<Guid> SeedScaleGraphAsync(
        MonkeysphereConnectionFactory connections,
        Guid recordTypeId,
        Guid relationshipTypeId)
    {
        const int recordCount = 10_000;
        const int relationshipCount = 50_000;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid[] recordIds = Enumerable.Range(0, recordCount)
            .Select(index => new Guid(index + 1, 0, 0, new byte[8]))
            .ToArray();

        await using SqliteConnection connection = await connections.OpenConnectionAsync();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using (SqliteCommand insertRecord = connection.CreateCommand())
        {
            insertRecord.Transaction = transaction;
            insertRecord.CommandText = """
                INSERT INTO Records (Id, RecordTypeId, DisplayName, CreatedAtUtc, UpdatedAtUtc)
                VALUES (@Id, @RecordTypeId, @DisplayName, @CreatedAtUtc, @UpdatedAtUtc);
                """;
            SqliteParameter id = insertRecord.Parameters.Add("@Id", SqliteType.Text);
            insertRecord.Parameters.AddWithValue("@RecordTypeId", recordTypeId.ToString("D"));
            SqliteParameter displayName = insertRecord.Parameters.Add("@DisplayName", SqliteType.Text);
            insertRecord.Parameters.AddWithValue("@CreatedAtUtc", now.ToString("O"));
            insertRecord.Parameters.AddWithValue("@UpdatedAtUtc", now.ToString("O"));
            for (int index = 0; index < recordIds.Length; index++)
            {
                id.Value = recordIds[index].ToString("D");
                displayName.Value = $"Record {index:D5}";
                await insertRecord.ExecuteNonQueryAsync();
            }
        }

        await using SqliteCommand insertRelationship = connection.CreateCommand();
        insertRelationship.Transaction = transaction;
        insertRelationship.CommandText = """
            INSERT INTO Relationships
                (Id, RelationshipTypeId, SourceRecordId, TargetRecordId, Note, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@Id, @RelationshipTypeId, @SourceRecordId, @TargetRecordId, NULL, @CreatedAtUtc, @UpdatedAtUtc);
            """;
        SqliteParameter relationshipId = insertRelationship.Parameters.Add("@Id", SqliteType.Text);
        insertRelationship.Parameters.AddWithValue("@RelationshipTypeId", relationshipTypeId.ToString("D"));
        SqliteParameter sourceId = insertRelationship.Parameters.Add("@SourceRecordId", SqliteType.Text);
        SqliteParameter targetId = insertRelationship.Parameters.Add("@TargetRecordId", SqliteType.Text);
        insertRelationship.Parameters.AddWithValue("@CreatedAtUtc", now.ToString("O"));
        insertRelationship.Parameters.AddWithValue("@UpdatedAtUtc", now.ToString("O"));
        int relationshipIndex = 0;

        async Task InsertAsync(int sourceIndex, int targetIndex)
        {
            relationshipId.Value = new Guid(100_001 + relationshipIndex, 0, 0, new byte[8]).ToString("D");
            sourceId.Value = recordIds[sourceIndex].ToString("D");
            targetId.Value = recordIds[targetIndex].ToString("D");
            await insertRelationship.ExecuteNonQueryAsync();
            relationshipIndex++;
        }

        for (int targetIndex = 1; targetIndex <= 600; targetIndex++)
        {
            await InsertAsync(0, targetIndex);
        }

        for (int sourceIndex = 1; sourceIndex <= 65; sourceIndex++)
        {
            for (int targetIndex = sourceIndex + 1; targetIndex <= 65; targetIndex++)
            {
                await InsertAsync(sourceIndex, targetIndex);
            }
        }

        for (int offset = 1; relationshipIndex < relationshipCount; offset++)
        {
            for (int sourceIndex = 601; sourceIndex < recordCount && relationshipIndex < relationshipCount; sourceIndex++)
            {
                int targetIndex = 601 + ((sourceIndex - 601 + offset) % (recordCount - 601));
                await InsertAsync(sourceIndex, targetIndex);
            }
        }

        await transaction.CommitAsync();
        Assert.Equal(relationshipCount, relationshipIndex);
        return recordIds[0];
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
