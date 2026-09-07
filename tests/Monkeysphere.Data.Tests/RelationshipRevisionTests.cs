using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

public sealed partial class RecordWorkflowTests
{
    [Fact]
    public async Task RelationshipRevisionsProtectSharedWritesAndSurviveRestart()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        RecordType person = await records.CreateRecordTypeAsync("Revision people");
        RecordDetails first = await records.CreateRecordAsync(person.Id, "First", []);
        RecordDetails second = await records.CreateRecordAsync(person.Id, "Second", []);
        RelationshipType type = await relationships.CreateTypeAsync(new("knows", RelationshipDirectionality.Symmetric));
        Assert.Matches("^[0-9a-f]{32}$", type.Revision);
        RelationshipView link = await relationships.CreateAsync(type.Id, first.Record.Id, second.Record.Id, expectedRevision: type.Revision);
        Assert.Matches("^[0-9a-f]{32}$", link.Revision);
        Assert.Equal(link.Revision, Assert.Single(await relationships.ListForRecordAsync(second.Record.Id)).Revision);

        await application.RestartAsync();
        relationships = application.Services.GetRequiredService<IRelationshipService>();
        Assert.Equal(type.Revision, Assert.Single(await relationships.ListTypesAsync()).Revision);
        Assert.Equal(link.Revision, Assert.Single((await relationships.QueryForRecordAsync(first.Record.Id)).Items).Revision);
        await relationships.RenameTypeAsync(type.Id, "is acquainted with", null, expectedRevision: type.Revision);
        RelationshipType renamed = Assert.Single(await relationships.ListTypesAsync());
        Assert.NotEqual(type.Revision, renamed.Revision);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => relationships.RenameTypeAsync(type.Id, "stale", null, expectedRevision: type.Revision));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => relationships.RetireTypeAsync(type.Id, expectedRevision: type.Revision));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => relationships.CreateAsync(type.Id, first.Record.Id, Guid.NewGuid(), expectedRevision: type.Revision));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => relationships.DeleteAsync(link.Id, expectedRevision: link.Revision));
        RelationshipView refreshed = Assert.Single(await relationships.ListForRecordAsync(first.Record.Id));
        Assert.NotEqual(link.Revision, refreshed.Revision);

        // Direct storage changes must invalidate a reviewed link even when its timestamp is unchanged.
        await using (SqliteConnection connection = await application.Services.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync())
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE Relationships SET Note = 'Changed note' WHERE Id = @Id;";
            command.Parameters.AddWithValue("@Id", link.Id.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => relationships.DeleteAsync(link.Id, expectedRevision: refreshed.Revision));
        RelationshipView changed = Assert.Single(await relationships.ListForRecordAsync(first.Record.Id));
        Assert.Equal("Changed note", changed.Note);
        Assert.True(await relationships.DeleteAsync(link.Id, expectedRevision: changed.Revision));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => relationships.DeleteAsync(link.Id, expectedRevision: changed.Revision));
        await relationships.RetireTypeAsync(type.Id, expectedRevision: renamed.Revision);
        Assert.NotEqual(renamed.Revision, Assert.Single(await relationships.ListTypesAsync()).Revision);
        await Assert.ThrowsAsync<DomainValidationException>(() => relationships.CreateAsync(type.Id, first.Record.Id, second.Record.Id));
        Assert.Empty(await relationships.ListForRecordAsync(first.Record.Id));
    }

    [Fact]
    public async Task ConcurrentRelationshipTypeEditsHaveExactlyOneWinner()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        RelationshipType type = await relationships.CreateTypeAsync(new("original", RelationshipDirectionality.Symmetric));
        Task<bool> EditAsync(string name) => Task.Run(async () =>
        {
            try
            {
                await relationships.RenameTypeAsync(type.Id, name, null, expectedRevision: type.Revision);
                return true;
            }
            catch (ConcurrencyConflictException) { return false; }
        });
        bool[] results = await Task.WhenAll(EditAsync("first"), EditAsync("second"));
        Assert.Single(results, succeeded => succeeded);
        Assert.Single(results, succeeded => !succeeded);
        Assert.True(Assert.Single(await relationships.ListTypesAsync()).Name is "first" or "second");
        await Assert.ThrowsAsync<DomainValidationException>(() => relationships.CreateTypeAsync(new("invalid", (RelationshipDirectionality)99, "inverse")));
        Assert.Single(await relationships.ListTypesAsync());
    }

}
