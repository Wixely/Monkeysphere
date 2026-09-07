using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Data;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task PresetDiscoveryIsPagedAndDoesNotPersistImplicitSetup()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        _ = await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>().CreateRecordTypeAsync("Existing custom type");
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);
        using JsonDocument stateResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_setup_state");
        RemoteSetupState state = Structured(stateResult).Deserialize<RemoteSetupState>(JsonOptions)!;
        Assert.True(state.Setup.IsComplete);
        Assert.Equal("existing", state.Setup.StarterPackKey);
        Assert.Null(state.Setup.CompletedAtUtc);
        Assert.Equal(0, state.InstalledPresetCount);
        await using var connection = await scope.ServiceProvider.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM SetupState;"));
        using JsonDocument repeated = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_setup_state");
        Assert.Equal(Structured(stateResult).GetRawText(), Structured(repeated).GetRawText());
        foreach (string name in new[] { "list_presets", "list_starter_packs", "list_relationship_presets" })
        {
            using JsonDocument page = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", name, new { page = 1, pageSize = 1 });
            Assert.Equal(state.CatalogRevision, Structured(page).GetProperty("catalogRevision").GetString());
            Assert.Equal(1, Structured(page).GetProperty("page").GetProperty("items").GetArrayLength());
            Assert.True(Structured(page).GetProperty("page").GetProperty("totalCount").GetInt32() > 1);
        }
        using JsonDocument denied = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "install_preset",
            new
            {
                domainId = MonkeysphereDomains.DefaultId,
                presetKey = "monkeysphere.person",
                expectedRevision = state.Revision,
                expectedCatalogRevision = state.CatalogRevision,
                idempotencyKey = Guid.CreateVersion7()
            });
        AssertWriteError(denied, "permission_denied");
        using JsonDocument deniedSetup = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "complete_setup",
            new
            {
                domainId = MonkeysphereDomains.DefaultId,
                starterPackKey = "blank",
                selectedPresetKeys = Array.Empty<string>(),
                expectedRevision = state.Revision,
                expectedCatalogRevision = state.CatalogRevision,
                idempotencyKey = Guid.CreateVersion7()
            });
        AssertWriteError(deniedSetup, "permission_denied");
    }

    [Fact]
    public async Task McpCompletesSelectedSetupAtomicallyAndReplaysConcurrentRetries()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        Guid domainId = (await scope.ServiceProvider.GetRequiredService<IDomainCatalog>().CreateAsync("Setup fixture")).Id;
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read", "structure.write"]);
        using JsonDocument stateResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_setup_state", new { domainId });
        RemoteSetupState state = Structured(stateResult).Deserialize<RemoteSetupState>(JsonOptions)!;
        Assert.False(state.Setup.IsComplete);
        using JsonDocument packsResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_starter_packs");
        StarterPack pack = Structured(packsResult).Deserialize<RemoteCatalogPage<StarterPack>>(JsonOptions)!.Page.Items.First(pack => pack.PresetKeys.Count >= 2);
        string[] selection = pack.PresetKeys.Take(2).ToArray();
        var complete = new
        {
            domainId,
            starterPackKey = pack.Key,
            selectedPresetKeys = selection,
            expectedRevision = state.Revision,
            expectedCatalogRevision = state.CatalogRevision,
            idempotencyKey = Guid.CreateVersion7()
        };
        using JsonDocument wrongDomainRevision = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "complete_setup",
            complete with { domainId = MonkeysphereDomains.DefaultId });
        AssertWriteError(wrongDomainRevision, "stale_revision");
        using JsonDocument duplicateSelection = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "complete_setup",
            complete with { selectedPresetKeys = [selection[0], selection[0]] });
        AssertWriteError(duplicateSelection, "validation_failed");
        JsonDocument[] results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "complete_setup", complete)));
        string original;
        try
        {
            original = Structured(results[0]).GetRawText();
            Assert.Equal(original, Structured(results[1]).GetRawText());
            RecordCommandReceipt receipt = Structured(results[0]).Deserialize<RecordCommandReceipt>(JsonOptions)!;
            Assert.Equal(domainId, receipt.Items[^1].Id);
            Assert.Equal("setup_completed", receipt.Items[^1].Outcome);
        }
        finally { foreach (JsonDocument result in results) result.Dispose(); }
        using JsonDocument installedResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_installed_presets", new { domainId, pageSize = 1 });
        PagedResult<InstalledPreset> installed = Structured(installedResult).Deserialize<PagedResult<InstalledPreset>>(JsonOptions)!;
        Assert.Equal(2, installed.TotalCount);
        Assert.Single(installed.Items);
        using JsonDocument other = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_installed_presets");
        Assert.Equal(0, Structured(other).GetProperty("totalCount").GetInt32());
        using JsonDocument changed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "complete_setup",
            complete with { selectedPresetKeys = selection.Take(1).ToArray() });
        AssertWriteError(changed, "retry_conflict");
        using JsonDocument stale = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "install_preset",
            new { domainId, presetKey = "monkeysphere.person", expectedRevision = state.Revision, expectedCatalogRevision = state.CatalogRevision, idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(stale, "stale_revision");
        using (scope.ServiceProvider.GetRequiredService<ICurrentDomainScope>().Use(domainId))
        {
            await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>().RenameRecordTypeAsync(installed.Items[0].RecordTypeId, "Locally renamed");
            await using var connection = await scope.ServiceProvider.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ApplicationCommandAudit WHERE Outcome = 'committed';"));
        }
        using JsonDocument replay = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "complete_setup", complete);
        Assert.Equal(original, Structured(replay).GetRawText());
    }

    [Fact]
    public async Task BlankSetupRequiresAcknowledgementAndAllowsLaterPresetInstallation()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read", "structure.write"]);
        using JsonDocument stateResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_setup_state");
        RemoteSetupState state = Structured(stateResult).Deserialize<RemoteSetupState>(JsonOptions)!;
        StarterPack blank = PresetCatalog.StarterPacks.Single(pack => pack.PresetKeys.Count == 0);
        var complete = new
        {
            domainId = MonkeysphereDomains.DefaultId,
            starterPackKey = blank.Key,
            selectedPresetKeys = Array.Empty<string>(),
            expectedRevision = state.Revision,
            expectedCatalogRevision = state.CatalogRevision,
            acknowledgeBlank = false,
            idempotencyKey = Guid.CreateVersion7()
        };
        using JsonDocument unconfirmed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "complete_setup", complete);
        AssertWriteError(unconfirmed, "validation_failed");
        using JsonDocument oldCatalog = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "complete_setup",
            complete with { acknowledgeBlank = true, expectedCatalogRevision = "old" });
        AssertWriteError(oldCatalog, "stale_revision");
        using JsonDocument completed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "complete_setup", complete with { acknowledgeBlank = true });
        RecordMutationOutcome result = Assert.Single(Structured(completed).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items);
        var install = new
        {
            complete.domainId,
            presetKey = "monkeysphere.person",
            expectedRevision = result.Revision,
            expectedCatalogRevision = state.CatalogRevision,
            idempotencyKey = Guid.CreateVersion7()
        };
        using JsonDocument installed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "install_preset", install);
        RecordCommandReceipt receipt = Structured(installed).Deserialize<RecordCommandReceipt>(JsonOptions)!;
        Assert.Contains(receipt.Items, item => item.Outcome == "created");
        using JsonDocument replay = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "install_preset", install);
        Assert.Equal(Structured(installed).GetRawText(), Structured(replay).GetRawText());
        using JsonDocument duplicate = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "install_preset",
            install with { expectedRevision = receipt.Items[^1].Revision, idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(duplicate, "validation_failed");
    }

    [Theory]
    [InlineData("structure.write")]
    [InlineData("instance.read")]
    public async Task OnboardingReadsDoNotInheritAuthorityFromOtherGrants(string grant)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);
        foreach (string name in new[] { "get_setup_state", "list_installed_presets", "list_presets", "list_starter_packs", "list_relationship_presets" })
        {
            using JsonDocument denied = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", name);
            Assert.True(denied.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        }
    }
}
