using Dapper;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed partial class SqliteMonkeysphereStore : IPresetCommandStore
{
    public Task<RecordCommandReceipt> InstallPresetsAsync(RecordCommandIdentity identity, string expectedRevision,
        PresetInstallation installation, CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, installation.StarterPackKey is null ? "presets.install" : "setup.complete",
            installation.InstalledAtUtc, async (connection, transaction) =>
        {
            PresetInspection before = await SqlitePresetStore.InspectCoreAsync(connection, transaction, currentDomain.Id, cancellationToken).ConfigureAwait(false);
            if (before.Revision != expectedRevision) throw new ConcurrencyConflictException("Domain setup or structures changed. Read setup state again before applying.");
            await SqlitePresetStore.InstallCoreAsync(connection, transaction, installation, cancellationToken).ConfigureAwait(false);
            List<RecordMutationOutcome> outcomes = [];
            foreach (RecordTypePresetInstallation item in installation.RecordTypes)
            {
                RecordType type = (await QueryRecordTypeAsync(connection, item.Id, transaction, cancellationToken).ConfigureAwait(false))!;
                outcomes.Add(new(type.Id, type.Revision, "created"));
            }
            foreach (RelationshipTypePresetInstallation item in installation.RelationshipTypes)
            {
                string revision = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                    "SELECT Revision FROM RelationshipTypes WHERE Id = @Id;", new { Id = Key(item.Id) }, transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Installed relationship type could not be read back.");
                outcomes.Add(new(item.Id, revision, "created"));
            }
            PresetInspection after = await SqlitePresetStore.InspectCoreAsync(connection, transaction, currentDomain.Id, cancellationToken).ConfigureAwait(false);
            outcomes.Add(new(identity.DomainId, after.Revision, installation.StarterPackKey is null ? "presets_installed" : "setup_completed"));
            return outcomes;
        }, cancellationToken);
}
