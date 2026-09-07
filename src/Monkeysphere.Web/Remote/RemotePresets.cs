using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

public sealed record RemoteCatalogPage<T>(string CatalogRevision, PagedResult<T> Page);
public sealed record RemoteSetupState(SetupStatus Setup, string Revision, string CatalogRevision, int InstalledPresetCount);

public sealed class RemotePresetQueries(IPresetStore presets, ICurrentDomainScope currentDomain, IHttpContextAccessor accessor)
{
    public async Task<RemoteSetupState> GetStateAsync(Guid? domainId, CancellationToken cancellationToken)
    {
        RemoteReadAuthorization.Demand(accessor);
        using IDisposable? domain = domainId is Guid id ? currentDomain.Use(id) : null;
        PresetInspection state = await presets.InspectAsync(cancellationToken).ConfigureAwait(false);
        return new(state.Setup, state.Revision, PresetContract.CatalogRevision, state.InstalledPresets.Count);
    }

    public async Task<PagedResult<InstalledPreset>> GetInstalledAsync(Guid? domainId, int page, int pageSize, CancellationToken cancellationToken)
    {
        RemoteReadAuthorization.Demand(accessor);
        DiscoveryPagination.Validate(page, pageSize);
        using IDisposable? domain = domainId is Guid id ? currentDomain.Use(id) : null;
        PresetInspection state = await presets.InspectAsync(cancellationToken).ConfigureAwait(false);
        return DiscoveryPagination.From(state.InstalledPresets, page, pageSize);
    }
}

[McpServerToolType]
[RemoteToolScopes("records.read")]
public sealed class MonkeyspherePresetReadTools
{
    [McpServerTool(Name = "get_setup_state", UseStructuredContent = true, ReadOnly = true)]
    [Description("Reads effective onboarding state, installed preset count, domain setup revision and packaged catalog revision without modifying data. Optional domainId defaults to Default. Existing custom structures imply completed onboarding even before the browser persists that state.")]
    public static Task<RemoteSetupState> GetStateAsync(RemotePresetQueries queries, Guid? domainId = null, CancellationToken cancellationToken = default) =>
        queries.GetStateAsync(domainId, cancellationToken);

    [McpServerTool(Name = "list_installed_presets", UseStructuredContent = true, ReadOnly = true)]
    [Description("Pages installed record-type presets with local IDs, recorded versions, names and active/retired state. Includes locally customized types; does not imply upgrade support. Optional domainId defaults to Default.")]
    public static Task<PagedResult<InstalledPreset>> InstalledAsync(RemotePresetQueries queries, int page = 1, int pageSize = 25,
        Guid? domainId = null, CancellationToken cancellationToken = default) => queries.GetInstalledAsync(domainId, page, pageSize, cancellationToken);

    [McpServerTool(Name = "list_presets", UseStructuredContent = true, ReadOnly = true)]
    [Description("Pages the packaged record-type preset catalog, including keys, versions, examples, fields, configuration and requiredness. Deployment-wide immutable catalog; no domain selector. Returns catalog revision. Requires records.read.")]
    public static RemoteCatalogPage<RecordTypePreset> PresetsAsync(IHttpContextAccessor accessor, int page = 1, int pageSize = 25)
    {
        RemoteReadAuthorization.Demand(accessor);
        return new(PresetContract.CatalogRevision, DiscoveryPagination.From(PresetCatalog.RecordTypes, page, pageSize));
    }

    [McpServerTool(Name = "list_starter_packs", UseStructuredContent = true, ReadOnly = true)]
    [Description("Pages packaged onboarding levels and their selectable preset keys, including the blank option. Deployment-wide catalog; no domain selector. Returns catalog revision. Requires records.read.")]
    public static RemoteCatalogPage<StarterPack> PacksAsync(IHttpContextAccessor accessor, int page = 1, int pageSize = 25)
    {
        RemoteReadAuthorization.Demand(accessor);
        return new(PresetContract.CatalogRevision, DiscoveryPagination.From(PresetCatalog.StarterPacks, page, pageSize));
    }

    [McpServerTool(Name = "list_relationship_presets", UseStructuredContent = true, ReadOnly = true)]
    [Description("Pages packaged relationship definitions and the required/alternative preset keys that determine installation with a selection. Deployment-wide catalog; no domain selector. Returns catalog revision. Requires records.read.")]
    public static RemoteCatalogPage<RelationshipTypePreset> RelationshipsAsync(IHttpContextAccessor accessor, int page = 1, int pageSize = 25)
    {
        RemoteReadAuthorization.Demand(accessor);
        return new(PresetContract.CatalogRevision, DiscoveryPagination.From(PresetCatalog.RelationshipTypes, page, pageSize));
    }
}

public sealed partial class RemoteRecordWriter
{
    public Task<CallToolResult> InstallPresetAsync(Guid domainId, string presetKey, string expectedRevision, string expectedCatalogRevision,
        Guid idempotencyKey, CancellationToken cancellationToken) => RunAsync(domainId, "presets.install", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, presetKey, expectedRevision, expectedCatalogRevision });
        RecordCommandIdentity identity = identities.Create(domainId, "structure.write", "presets.install", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await presetCommands.InstallAsync(identity, presetKey, expectedRevision, expectedCatalogRevision, cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> CompleteSetupAsync(Guid domainId, string starterPackKey, IReadOnlyList<string> selectedPresetKeys,
        string expectedRevision, string expectedCatalogRevision, bool acknowledgeBlank, Guid idempotencyKey,
        CancellationToken cancellationToken) => RunAsync(domainId, "setup.complete", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, starterPackKey, selectedPresetKeys, expectedRevision, expectedCatalogRevision, acknowledgeBlank });
        RecordCommandIdentity identity = identities.Create(domainId, "structure.write", "setup.complete", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await presetCommands.CompleteAsync(identity, starterPackKey, selectedPresetKeys, expectedRevision,
            expectedCatalogRevision, acknowledgeBlank, cancellationToken).ConfigureAwait(false);
    });
}

[McpServerToolType]
[RemoteToolScopes("structure.write")]
public sealed class MonkeyspherePresetWriteTools
{
    [McpServerTool(Name = "install_preset", ReadOnly = false, Destructive = false)]
    [Description("Installs one packaged record-type preset in an explicit domain using shared preset rules. Requires structure.write, setup and catalog revisions from discovery, and idempotencyKey. Creates its fields and applicable relationships atomically with receipt/audit. Existing presets or conflicting names fail. Does not upgrade or replace local types.")]
    public static Task<CallToolResult> InstallAsync(RemoteRecordWriter writer, Guid domainId, string presetKey, string expectedRevision,
        string expectedCatalogRevision, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        writer.InstallPresetAsync(domainId, presetKey, expectedRevision, expectedCatalogRevision, idempotencyKey, cancellationToken);

    [McpServerTool(Name = "complete_setup", ReadOnly = false, Destructive = false)]
    [Description("Completes onboarding with an explicit starter pack and a distinct subset of its preset keys. Requires structure.write, explicit domainId, setup/catalog revisions and idempotencyKey. Empty selection requires acknowledgeBlank=true; preset-dependent features may need presets installed later. Entire selection, completion state, receipt and audit commit atomically; identical retries replay for 24 hours.")]
    public static Task<CallToolResult> CompleteAsync(RemoteRecordWriter writer, Guid domainId, string starterPackKey,
        IReadOnlyList<string> selectedPresetKeys, string expectedRevision, string expectedCatalogRevision, Guid idempotencyKey,
        bool acknowledgeBlank = false, CancellationToken cancellationToken = default) =>
        writer.CompleteSetupAsync(domainId, starterPackKey, selectedPresetKeys, expectedRevision, expectedCatalogRevision, acknowledgeBlank, idempotencyKey, cancellationToken);
}
