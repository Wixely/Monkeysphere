using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

public sealed partial class RecordWorkflowTests
{
    [Fact]
    public async Task SchemaReceiptsAndFieldRevisionsSurviveRestartAndCompetingEdits()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        StructureCommandService commands = application.Services.GetRequiredService<StructureCommandService>();
        RecordCommandIdentity typeIdentity = RelationshipIdentity("record_types.create");
        RecordCommandReceipt typeReceipt = await commands.CreateTypeAsync(typeIdentity, "Receipt type", null);
        RecordMutationOutcome type = Assert.Single(typeReceipt.Items);
        RecordCommandIdentity fieldIdentity = RelationshipIdentity("fields.create_attach");
        RecordCommandReceipt fieldReceipt = await commands.CreateFieldAsync(fieldIdentity, type.Id, type.Revision,
            new("Custom", "custom.extension", false));
        RecordMutationOutcome field = fieldReceipt.Items[0];
        await application.RestartAsync();
        commands = application.Services.GetRequiredService<StructureCommandService>();
        RecordCommandReceipt typeReplay = await commands.CreateTypeAsync(typeIdentity, "Receipt type", null);
        Assert.Equal(typeReceipt.Items.ToArray(), typeReplay.Items.ToArray());
        RecordCommandReceipt fieldReplay = await commands.CreateFieldAsync(fieldIdentity, type.Id, type.Revision, new("Custom", "custom.extension", false));
        Assert.Equal(fieldReceipt.Items.ToArray(), fieldReplay.Items.ToArray());
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        Assert.Equal(field.Revision, Assert.Single(await records.ListFieldDefinitionsAsync()).Revision);
        Assert.Matches("^[0-9a-f]{32}$", field.Revision);
        RecordType target = await records.CreateRecordTypeAsync("Attachment target");
        RecordCommandIdentity attachIdentity = RelationshipIdentity("fields.attach");
        RecordCommandReceipt attached = await commands.AttachFieldAsync(attachIdentity, target.Id, field.Id, target.Revision, field.Revision, false);
        await application.RestartAsync();
        commands = application.Services.GetRequiredService<StructureCommandService>();
        RecordCommandReceipt attachReplay = await commands.AttachFieldAsync(attachIdentity, target.Id, field.Id, target.Revision, field.Revision, false);
        Assert.Equal(attached.Items.ToArray(), attachReplay.Items.ToArray());
        records = application.Services.GetRequiredService<IMonkeysphereService>();
        await records.RenameFieldAsync(field.Id, "Renamed");
        Assert.NotEqual(field.Revision, Assert.Single(await records.ListFieldDefinitionsAsync()).Revision);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => records.CreateAndAttachFieldAsync(target.Id,
            new("Stale browser field", "text", false), expectedRevision: attached.Items[0].Revision));
        RecordType another = await records.CreateRecordTypeAsync("Another target");
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => records.AttachFieldAsync(another.Id, field.Id, false,
            expectedRevision: another.Revision, expectedFieldRevision: field.Revision));
        Assert.Empty((await records.GetRecordTypeAsync(another.Id))!.Fields);
        string currentRevision = (await records.GetRecordTypeAsync(target.Id))!.RecordType.Revision;
        Task<bool> AddAsync(string name) => Task.Run(async () =>
        {
            try
            {
                _ = await commands.CreateFieldAsync(RelationshipIdentity("fields.create_attach"), target.Id, currentRevision, new(name, "text", false));
                return true;
            }
            catch (ConcurrencyConflictException) { return false; }
        });
        bool[] results = await Task.WhenAll(AddAsync("First"), AddAsync("Second"));
        Assert.Single(results, succeeded => succeeded);
        Assert.Single(results, succeeded => !succeeded);
        Assert.Equal(2, (await records.GetRecordTypeAsync(target.Id))!.Fields.Count);
    }

    [Fact]
    public async Task SchemaCommandsRollBackMutationsAndRevisionsWhenReceiptAuditFails()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        StructureCommandService commands = application.Services.GetRequiredService<StructureCommandService>();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType source = await records.CreateRecordTypeAsync("Source type");
        FieldDefinition field = await records.CreateAndAttachFieldAsync(source.Id, new("Reusable", "text", false));
        RecordType target = await records.CreateRecordTypeAsync("Empty target");
        await using SqliteConnection connection = await application.Services.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        await connection.ExecuteAsync("CREATE TRIGGER TestRejectSchemaAudit BEFORE INSERT ON ApplicationCommandAudit BEGIN SELECT RAISE(ABORT, 'Test failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => commands.CreateTypeAsync(RelationshipIdentity("record_types.create"), "Failed type", null));
        await Assert.ThrowsAsync<SqliteException>(() => commands.CreateFieldAsync(RelationshipIdentity("fields.create_attach"), target.Id,
            target.Revision, new("Failed field", "text", false)));
        await Assert.ThrowsAsync<SqliteException>(() => commands.AttachFieldAsync(RelationshipIdentity("fields.attach"), target.Id,
            field.Id, target.Revision, field.Revision, false));
        Assert.Equal(2, (await records.ListRecordTypesAsync()).Count);
        Assert.Single(await records.ListFieldDefinitionsAsync());
        RecordTypeDetails unchanged = (await records.GetRecordTypeAsync(target.Id))!;
        Assert.Equal(target.Revision, unchanged.RecordType.Revision);
        Assert.Empty(unchanged.Fields);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RecordCommandReceipts;"));
        await connection.ExecuteAsync("DROP TRIGGER TestRejectSchemaAudit;");
        _ = await records.CreateRecordAsync(target.Id, "Existing", []);
        await Assert.ThrowsAsync<DomainValidationException>(() => records.CreateAndAttachFieldAsync(target.Id, new("Required gap", "text", true)));
        await Assert.ThrowsAsync<DomainValidationException>(() => records.AttachFieldAsync(target.Id, field.Id, true));
        Assert.Single(await records.ListFieldDefinitionsAsync());
        Assert.Empty((await records.GetRecordTypeAsync(target.Id))!.Fields);
        await records.AttachFieldAsync(target.Id, field.Id, false);
        Assert.Single((await records.GetRecordTypeAsync(target.Id))!.Fields);
    }
}
