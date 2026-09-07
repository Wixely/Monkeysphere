using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

public sealed partial class RecordWorkflowTests
{
    [Fact]
    public async Task RelationshipCommandsReplayAcrossRestartAndShareReceiptRetention()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RelationshipCommandService commands = application.Services.GetRequiredService<RelationshipCommandService>();
        RecordType recordType = await records.CreateRecordTypeAsync("Receipt people");
        RecordDetails first = await records.CreateRecordAsync(recordType.Id, "First", []);
        RecordDetails second = await records.CreateRecordAsync(recordType.Id, "Second", []);
        RecordCommandIdentity typeIdentity = RelationshipIdentity("relationship_types.create");
        RecordCommandReceipt definition = await commands.CreateTypeAsync(typeIdentity, new("knows", RelationshipDirectionality.Symmetric));
        RecordMutationOutcome type = Assert.Single(definition.Items);
        RecordCommandIdentity createIdentity = RelationshipIdentity("relationships.create");
        RecordCommandReceipt created = await commands.CreateAsync(createIdentity, type.Id, first.Record.Id, second.Record.Id,
            type.Revision, first.Revision, second.Revision);
        RecordMutationOutcome link = Assert.Single(created.Items);
        await application.RestartAsync();
        commands = application.Services.GetRequiredService<RelationshipCommandService>();
        RecordCommandReceipt definitionReplay = await commands.CreateTypeAsync(typeIdentity, new("knows", RelationshipDirectionality.Symmetric));
        Assert.Equal(definition.Items.ToArray(), definitionReplay.Items.ToArray());
        RecordCommandIdentity deleteIdentity = RelationshipIdentity("relationships.delete");
        RecordCommandReceipt deleted = await commands.DeleteAsync(deleteIdentity, link.Id, link.Revision);
        await application.RestartAsync();
        commands = application.Services.GetRequiredService<RelationshipCommandService>();
        RecordCommandReceipt deleteReplay = await commands.DeleteAsync(deleteIdentity, link.Id, link.Revision);
        Assert.Equal(deleted.Items.ToArray(), deleteReplay.Items.ToArray());
        Assert.Equal(deleted.CompletedAtUtc, deleteReplay.CompletedAtUtc);
        RecordCommandReceipt createReplay = await commands.CreateAsync(createIdentity, type.Id, first.Record.Id, second.Record.Id,
            type.Revision, first.Revision, second.Revision);
        Assert.Equal(created.Items.ToArray(), createReplay.Items.ToArray());
        Assert.Empty(await application.Services.GetRequiredService<IRelationshipService>().ListForRecordAsync(first.Record.Id));
        IRecordCommandStore receipts = application.Services.GetRequiredService<IRecordCommandStore>();
        Assert.Null(await receipts.GetReceiptAsync(createIdentity with { CredentialFingerprint = new string('C', 64) }, created.CompletedAtUtc));
        CommandReplayException conflict = await Assert.ThrowsAsync<CommandReplayException>(() => receipts.GetReceiptAsync(
            createIdentity with { RequestHash = new string('D', 64) }, created.CompletedAtUtc));
        Assert.Equal("retry_conflict", conflict.Code);
        CommandReplayException expired = await Assert.ThrowsAsync<CommandReplayException>(() => receipts.GetReceiptAsync(createIdentity, created.RetryUntilUtc));
        Assert.Equal("retry_expired", expired.Code);
        await Assert.ThrowsAsync<DomainValidationException>(() => commands.DeleteAsync(deleteIdentity with { DomainId = Guid.NewGuid() }, link.Id, link.Revision));
        await Assert.ThrowsAsync<DomainValidationException>(() => commands.DeleteAsync(createIdentity, link.Id, link.Revision));
    }

    [Fact]
    public async Task RelationshipMutationsRollBackWhenAtomicAuditCannotBeWritten()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        RelationshipCommandService commands = application.Services.GetRequiredService<RelationshipCommandService>();
        RecordType recordType = await records.CreateRecordTypeAsync("Atomic people");
        RecordDetails first = await records.CreateRecordAsync(recordType.Id, "First", []);
        RecordDetails second = await records.CreateRecordAsync(recordType.Id, "Second", []);
        await using SqliteConnection connection = await application.Services.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        const string rejectAudit = "CREATE TRIGGER TestRejectRelationshipAudit BEFORE INSERT ON ApplicationCommandAudit BEGIN SELECT RAISE(ABORT, 'Test audit failure'); END;";
        const string allowAudit = "DROP TRIGGER TestRejectRelationshipAudit;";
        await connection.ExecuteAsync(rejectAudit);
        await Assert.ThrowsAsync<SqliteException>(() => commands.CreateTypeAsync(RelationshipIdentity("relationship_types.create"), new("failed", RelationshipDirectionality.Symmetric)));
        Assert.Empty(await relationships.ListTypesAsync());
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RecordCommandReceipts;"));
        await connection.ExecuteAsync(allowAudit);
        RelationshipType type = await relationships.CreateTypeAsync(new("knows", RelationshipDirectionality.Symmetric));
        await connection.ExecuteAsync(rejectAudit);
        RecordCommandIdentity createIdentity = RelationshipIdentity("relationships.create");
        await Assert.ThrowsAsync<SqliteException>(() => commands.CreateAsync(createIdentity, type.Id, first.Record.Id, second.Record.Id,
            type.Revision, first.Revision, second.Revision));
        Assert.Empty(await relationships.ListForRecordAsync(first.Record.Id));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RecordCommandReceipts;"));
        await connection.ExecuteAsync(allowAudit);
        RecordCommandReceipt created = await commands.CreateAsync(createIdentity, type.Id, first.Record.Id, second.Record.Id,
            type.Revision, first.Revision, second.Revision);
        RecordMutationOutcome link = Assert.Single(created.Items);
        await connection.ExecuteAsync(rejectAudit);
        RecordCommandIdentity deleteIdentity = RelationshipIdentity("relationships.delete");
        await Assert.ThrowsAsync<SqliteException>(() => commands.DeleteAsync(deleteIdentity, link.Id, link.Revision));
        Assert.Equal(link.Revision, Assert.Single(await relationships.ListForRecordAsync(first.Record.Id)).Revision);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RecordCommandReceipts;"));
        await connection.ExecuteAsync(allowAudit);
        _ = await commands.DeleteAsync(deleteIdentity, link.Id, link.Revision);
        Assert.Empty(await relationships.ListForRecordAsync(first.Record.Id));
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ApplicationCommandAudit;"));
    }

    private static RecordCommandIdentity RelationshipIdentity(string action) =>
        new(MonkeysphereDomains.DefaultId, "mcp", new string('A', 64), action, Guid.CreateVersion7(), new string('B', 64));
}
