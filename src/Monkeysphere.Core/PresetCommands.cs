namespace Monkeysphere.Core;

public sealed record InstalledPreset(Guid RecordTypeId, string Key, int? Version, string Name, string Lifecycle);
public sealed record PresetInspection(SetupStatus Setup, string Revision, IReadOnlyList<InstalledPreset> InstalledPresets);

public static class PresetContract
{
    public static string CatalogRevision { get; } = CommandRequestHash.Compute(new
    {
        PresetCatalog.RecordTypes,
        PresetCatalog.RelationshipTypes,
        PresetCatalog.StarterPacks,
    });
}

public interface IPresetCommandStore
{
    Task<RecordCommandReceipt> InstallPresetsAsync(RecordCommandIdentity identity, string expectedRevision,
        PresetInstallation installation, CancellationToken cancellationToken = default);
}

public sealed class PresetCommandService(IPresetStore presets, IPresetCommandStore commands, IRecordCommandStore receipts, TimeProvider timeProvider)
{
    public async Task<RecordCommandReceipt> InstallAsync(RecordCommandIdentity identity, string presetKey, string expectedRevision,
        string expectedCatalogRevision, CancellationToken cancellationToken = default)
    {
        if (identity.Action != "presets.install") throw new DomainValidationException("The command action does not match preset installation.");
        RecordCommandReceipt? replay = await receipts.GetReceiptAsync(identity, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (replay is not null) return replay;
        RequireRevisions(expectedRevision, expectedCatalogRevision);
        PresetService service = new(presets, timeProvider);
        return await commands.InstallPresetsAsync(identity, expectedRevision,
            service.CreateInstallation(null, [PresetService.FindPreset(presetKey)]), cancellationToken).ConfigureAwait(false);
    }

    public async Task<RecordCommandReceipt> CompleteAsync(RecordCommandIdentity identity, string starterPackKey,
        IReadOnlyList<string> selectedPresetKeys, string expectedRevision, string expectedCatalogRevision, bool acknowledgeBlank,
        CancellationToken cancellationToken = default)
    {
        if (identity.Action != "setup.complete") throw new DomainValidationException("The command action does not match setup completion.");
        RecordCommandReceipt? replay = await receipts.GetReceiptAsync(identity, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (replay is not null) return replay;
        RequireRevisions(expectedRevision, expectedCatalogRevision);
        StarterPack pack = PresetCatalog.StarterPacks.SingleOrDefault(pack => pack.Key == starterPackKey)
            ?? throw new DomainValidationException("Starter pack was not found.");
        if (selectedPresetKeys is null || selectedPresetKeys.Count > PresetCatalog.RecordTypes.Count ||
            selectedPresetKeys.Distinct(StringComparer.Ordinal).Count() != selectedPresetKeys.Count ||
            selectedPresetKeys.Any(key => !pack.PresetKeys.Contains(key, StringComparer.Ordinal)))
            throw new DomainValidationException("Select distinct presets from the chosen starter pack.");
        if (selectedPresetKeys.Count == 0 && !acknowledgeBlank)
            throw new DomainValidationException("A blank setup requires acknowledgeBlank=true. Preset-dependent features may need presets installed later.");
        PresetService service = new(presets, timeProvider);
        RecordTypePreset[] selected = pack.PresetKeys.Where(selectedPresetKeys.Contains).Select(PresetService.FindPreset).ToArray();
        return await commands.InstallPresetsAsync(identity, expectedRevision, service.CreateInstallation(pack.Key, selected), cancellationToken).ConfigureAwait(false);
    }

    private static void RequireRevisions(string revision, string catalogRevision)
    {
        if (string.IsNullOrWhiteSpace(revision) || revision.Length > 128) throw new DomainValidationException("Supply the revision returned by get_setup_state.");
        if (catalogRevision != PresetContract.CatalogRevision) throw new ConcurrencyConflictException("The preset catalog changed. Read it again before applying.");
    }
}
