using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

public sealed partial class RecordWorkflowTests
{
    [Fact]
    public async Task PresetCompletionReceiptsSurviveRestartAndLaterSchemaChanges()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IPresetStore presets = application.Services.GetRequiredService<IPresetStore>();
        PresetCommandService commands = application.Services.GetRequiredService<PresetCommandService>();
        PresetInspection before = await presets.InspectAsync();
        StarterPack pack = PresetCatalog.StarterPacks.First(pack => pack.PresetKeys.Count > 0);
        RecordCommandIdentity identity = RelationshipIdentity("setup.complete");
        RecordCommandReceipt receipt = await commands.CompleteAsync(identity, pack.Key, pack.PresetKeys, before.Revision, PresetContract.CatalogRevision, false);
        PresetInspection after = await presets.InspectAsync();
        Assert.True(after.Setup.IsComplete);
        Assert.NotEqual(before.Revision, after.Revision);
        Assert.Equal(pack.PresetKeys.Count, after.InstalledPresets.Count);
        await application.RestartAsync();
        presets = application.Services.GetRequiredService<IPresetStore>();
        Assert.Equal(after.Revision, (await presets.InspectAsync()).Revision);
        commands = application.Services.GetRequiredService<PresetCommandService>();
        RecordCommandReceipt replay = await commands.CompleteAsync(identity, pack.Key, pack.PresetKeys, before.Revision, PresetContract.CatalogRevision, false);
        Assert.Equal(receipt.Items.ToArray(), replay.Items.ToArray());
        Assert.Equal(receipt.CompletedAtUtc, replay.CompletedAtUtc);
        await application.Services.GetRequiredService<IMonkeysphereService>().RenameRecordTypeAsync(after.InstalledPresets[0].RecordTypeId, "Customized preset");
        Assert.NotEqual(after.Revision, (await presets.InspectAsync()).Revision);
        // Existing receipts must be looked up before validating a catalog that could have changed after an upgrade.
        RecordCommandReceipt priorCatalogReplay = await commands.CompleteAsync(identity, pack.Key, pack.PresetKeys, before.Revision, "previous catalog", false);
        Assert.Equal(receipt.Items.ToArray(), priorCatalogReplay.Items.ToArray());
        await Assert.ThrowsAsync<CommandReplayException>(() => commands.CompleteAsync(identity with { RequestHash = new string('D', 64) },
            pack.Key, pack.PresetKeys, before.Revision, PresetContract.CatalogRevision, false));
    }

    [Fact]
    public async Task PresetAuditFailureRollsBackEntireSelectionAndCompletionState()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IPresetStore presets = application.Services.GetRequiredService<IPresetStore>();
        PresetCommandService commands = application.Services.GetRequiredService<PresetCommandService>();
        PresetInspection before = await presets.InspectAsync();
        StarterPack pack = PresetCatalog.StarterPacks.OrderByDescending(pack => pack.PresetKeys.Count).First();
        await using SqliteConnection connection = await application.Services.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        await connection.ExecuteAsync("CREATE TRIGGER TestRejectPresetAudit BEFORE INSERT ON ApplicationCommandAudit BEGIN SELECT RAISE(ABORT, 'Test failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => commands.CompleteAsync(RelationshipIdentity("setup.complete"), pack.Key,
            pack.PresetKeys, before.Revision, PresetContract.CatalogRevision, false));
        Assert.Equal(before.Revision, (await presets.InspectAsync()).Revision);
        foreach (string table in new[] { "RecordTypes", "FieldDefinitions", "RelationshipTypes", "SetupState", "RecordCommandReceipts" })
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM " + table + ";"));
        await connection.ExecuteAsync("DROP TRIGGER TestRejectPresetAudit;");
        RecordCommandIdentity install = RelationshipIdentity("presets.install");
        _ = await commands.InstallAsync(install, pack.PresetKeys[0], before.Revision, PresetContract.CatalogRevision);
        PresetInspection installed = await presets.InspectAsync();
        Assert.True(installed.Setup.IsComplete);
        Assert.Equal("existing", installed.Setup.StarterPackKey);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM SetupState;"));
        await Assert.ThrowsAsync<DomainValidationException>(() => commands.CompleteAsync(RelationshipIdentity("setup.complete"),
            pack.Key, [], installed.Revision, PresetContract.CatalogRevision, true));
    }
}
