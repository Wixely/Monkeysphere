using System.Text;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

public sealed partial class RecordWorkflowTests
{
    [Fact]
    public async Task ImportRevisionRejectsChangesAfterPreparationAndSurvivesRestart()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        await application.Services.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        IVCardService vcards = application.Services.GetRequiredService<IVCardService>();
        IVCardStore store = application.Services.GetRequiredService<IVCardStore>();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        byte[] bytes = Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nFN:Reviewed person\nEMAIL:review@example.test\nEND:VCARD\n");
        VCardImportPreview preview = await vcards.PreviewAsync(bytes);
        IReadOnlyList<VCardImportSelection> choices = [new(0, VCardImportAction.CreateSeparately)];
        IReadOnlyList<VCardPreparedImport> prepared = await vcards.PrepareImportAsync(preview, choices);
        RecordDetails unrelated = await records.CreateRecordAsync(preview.RecordTypeId, "Concurrent person", []);
        Assert.NotEqual(preview.Revision, await store.GetImportRevisionAsync());
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.ApplyAsync(prepared, preview.Revision, DateTimeOffset.UtcNow));
        Assert.Equal(1, (await records.SearchRecordsAsync(new())).TotalCount);
        VCardImportPreview fresh = await vcards.PreviewAsync(bytes);
        prepared = await vcards.PrepareImportAsync(fresh, choices);
        _ = await records.UpdateRecordAsync(unrelated.Record.Id, "Concurrent person", [], ["New alias"]);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.ApplyAsync(prepared, fresh.Revision, DateTimeOffset.UtcNow));
        fresh = await vcards.PreviewAsync(bytes);
        string stable = fresh.Revision;
        await application.RestartAsync();
        vcards = application.Services.GetRequiredService<IVCardService>();
        store = application.Services.GetRequiredService<IVCardStore>();
        Assert.Equal(stable, await store.GetImportRevisionAsync());
        Assert.Equal(1, (await vcards.ApplyAsync(fresh, choices)).Created);
        Assert.NotEqual(stable, await store.GetImportRevisionAsync());
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => vcards.ApplyAsync(fresh, choices));
    }

    [Fact]
    public async Task ImportRevisionCoversSchemaProvenanceAndRolledBackWrites()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        await application.Services.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        IVCardService vcards = application.Services.GetRequiredService<IVCardService>();
        IVCardStore store = application.Services.GetRequiredService<IVCardStore>();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        byte[] bytes = Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nFN:Schema fixture\nEND:VCARD\n");
        VCardImportPreview initial = await vcards.PreviewAsync(bytes);
        RecordDetails person = await records.CreateRecordAsync(initial.RecordTypeId, "Existing person", []);
        using MemoryStream stream = new(bytes);
        VCardImportPreview preview = await vcards.PreviewAsync(stream);
        Assert.Equal((await vcards.PreviewAsync(bytes)).Revision, preview.Revision);
        RecordTypeDetails type = (await records.GetRecordTypeAsync(preview.RecordTypeId))!;
        FieldDefinition field = type.Fields[0].Definition;
        await records.RenameFieldAsync(field.Id, field.Name + " changed");
        Assert.NotEqual(preview.Revision, await store.GetImportRevisionAsync());
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => vcards.ApplyAsync(preview, [new(0, VCardImportAction.CreateSeparately)]));
        preview = await vcards.PreviewAsync(bytes);
        await using var connection = await application.Services.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        await connection.ExecuteAsync("INSERT INTO VCardImports (Fingerprint, RecordId, SourceVersion, ImportedAtUtc) VALUES (@Fingerprint, @Id, '4.0', @Now);",
            new { Fingerprint = new string('A', 64), Id = person.Record.Id.ToString("D"), Now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) });
        Assert.NotEqual(preview.Revision, await store.GetImportRevisionAsync());
        string beforeRollback = await store.GetImportRevisionAsync();
        using (var transaction = connection.BeginTransaction())
        {
            await connection.ExecuteAsync("UPDATE Records SET DisplayName = 'Rolled back' WHERE Id = @Id;", new { Id = person.Record.Id.ToString("D") }, transaction);
            Assert.NotEqual(beforeRollback, await connection.QuerySingleAsync<string>("SELECT Revision FROM ContactImportState;", transaction: transaction));
            await transaction.RollbackAsync();
        }
        Assert.Equal(beforeRollback, await store.GetImportRevisionAsync());
        VCardImportPreview pending = await vcards.PreviewAsync(bytes);
        IReadOnlyList<VCardPreparedImport> prepared = await vcards.PrepareImportAsync(pending, [new(0, VCardImportAction.CreateSeparately)]);
        await connection.ExecuteAsync("CREATE TRIGGER TestFailImport BEFORE INSERT ON VCardProperties BEGIN SELECT RAISE(ABORT, 'Test failure'); END;");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => store.ApplyAsync(prepared, pending.Revision, DateTimeOffset.UtcNow));
        Assert.Equal(pending.Revision, await store.GetImportRevisionAsync());
        Assert.Equal(1, (await records.SearchRecordsAsync(new())).TotalCount);
    }

    [Fact]
    public async Task ConcurrentPreparedImportsHaveOneWinner()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        await application.Services.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        IVCardService vcards = application.Services.GetRequiredService<IVCardService>();
        IVCardStore store = application.Services.GetRequiredService<IVCardStore>();
        VCardImportPreview preview = await vcards.PreviewAsync(Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nFN:One import\nEND:VCARD\n"));
        IReadOnlyList<VCardPreparedImport> prepared = await vcards.PrepareImportAsync(preview, [new(0, VCardImportAction.CreateSeparately)]);
        async Task<bool> ApplyAsync()
        {
            try { _ = await store.ApplyAsync(prepared, preview.Revision, DateTimeOffset.UtcNow); return true; }
            catch (ConcurrencyConflictException) { return false; }
        }
        bool[] results = await Task.WhenAll(Task.Run(ApplyAsync), Task.Run(ApplyAsync));
        Assert.Single(results, result => result);
        Assert.Equal(1, (await application.Services.GetRequiredService<IMonkeysphereService>().SearchRecordsAsync(new())).TotalCount);
    }
}
