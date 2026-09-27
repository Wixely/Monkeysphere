using System.Text.Json;
using DnaX.RemoteAccess;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Data;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// The merge over MCP. Two things are worth proving here beyond that it works: that it cannot be reached
/// with only half the permission it needs, and that a retry of a command that deletes a record replays
/// rather than repeats.
/// </summary>
public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task McpMergeNeedsBothWritingAndDeletingAndNeitherAloneRevealsAnything()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Merge fixture");
        RecordDetails keep = await records.CreateRecordAsync(type.Id, "Confidential survivor", []);
        RecordDetails lose = await records.CreateRecordAsync(type.Id, "Confidential duplicate", []);

        IDnaXRemoteAccessAdministration administration = factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        DnaXGeneratedCredential writeOnly = await administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, 0, ["records.write"]);
        DnaXRemoteEffectiveSurface surface = await administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, writeOnly.Version);
        Guid domainId = MonkeysphereDomains.DefaultId;
        var preview = new { domainId, survivingRecordId = keep.Record.Id, mergedRecordId = lose.Record.Id };

        // A merge deletes a record, so editing alone must not reach it: otherwise a credential that may
        // only edit could delete anything by merging it away.
        using JsonDocument deniedWriter = await SendAsync(client, surface.EndpointPath!, writeOnly.Secret, "tools/call", "preview_record_merge", preview);
        AssertWriteError(deniedWriter, "permission_denied");
        Assert.DoesNotContain("Confidential", deniedWriter.RootElement.GetRawText(), StringComparison.Ordinal);

        // And deleting alone must not reach it either, because a merge writes another record's data onto
        // the survivor, which deleting does not permit.
        RemoteCredentialManager manager = factory.Services.GetRequiredService<RemoteCredentialManager>();
        DnaXGeneratedCredential deleteOnly = await manager.RotateAsync(DnaXRemoteSurface.Mcp, surface.Version, ["records.delete"]);
        using JsonDocument deniedDeleter = await SendAsync(client, surface.EndpointPath!, deleteOnly.Secret, "tools/call", "preview_record_merge", preview);
        AssertWriteError(deniedDeleter, "permission_denied");
        Assert.DoesNotContain("Confidential", deniedDeleter.RootElement.GetRawText(), StringComparison.Ordinal);

        using JsonDocument deniedApply = await SendAsync(client, surface.EndpointPath!, deleteOnly.Secret, "tools/call", "merge_records",
            new { domainId, survivingRecordId = keep.Record.Id, mergedRecordId = lose.Record.Id, expectedRevision = "x", idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(deniedApply, "permission_denied");
        Assert.NotNull(await records.GetRecordAsync(lose.Record.Id));

        DnaXGeneratedCredential both = await manager.RotateAsync(DnaXRemoteSurface.Mcp, deleteOnly.Version, ["records.write", "records.delete"]);
        using JsonDocument discovery = await SendAsync(client, surface.EndpointPath!, both.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities capabilities = Structured(discovery).Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.Equal("1.38", capabilities.ContractVersion);
        Assert.True(capabilities.Tools.Single(tool => tool.Name == "merge_records").Allowed);
        Assert.True(capabilities.Tools.Single(tool => tool.Name == "preview_record_merge").Allowed);

        // Still no reading of record content, which a merge does not need and does not grant.
        Assert.False(capabilities.Tools.Single(tool => tool.Name == "get_record").Allowed);

        using JsonDocument allowed = await SendAsync(client, surface.EndpointPath!, both.Secret, "tools/call", "preview_record_merge", preview);
        Assert.Null(Structured(allowed).Deserialize<RemoteMergePreview>(JsonOptions)!.Refusal);
    }

    [Fact]
    public async Task McpMergeCarriesTheValuesItSaidItWouldAndReplaysARetryRatherThanRepeatingIt()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Merge apply fixture");
        FieldDefinition birthday = await records.CreateAndAttachFieldAsync(type.Id, new("Date of birth", FieldTypes.ExactDate, false));
        FieldDefinition phone = await records.CreateAndAttachFieldAsync(type.Id, new("Phone", FieldTypes.Text, false));
        RecordDetails keep = await records.CreateRecordAsync(type.Id, "Ada Lovelace", [new(birthday.Id, "1815-12-10")]);
        RecordDetails lose = await records.CreateRecordAsync(type.Id, "Augusta Ada King",
            [new(birthday.Id, "1815-12-11"), new(phone.Id, "555-0100")]);

        IDnaXRemoteAccessAdministration administration = factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        DnaXGeneratedCredential credential = await administration.RotateCredentialAsync(
            DnaXRemoteSurface.Mcp, 0, ["records.write", "records.delete", "contacts.export"]);
        DnaXRemoteEffectiveSurface surface = await administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, credential.Version);
        Guid domainId = MonkeysphereDomains.DefaultId;

        using JsonDocument previewed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_record_merge",
            new { domainId, survivingRecordId = keep.Record.Id, mergedRecordId = lose.Record.Id });
        RemoteMergePreview preview = Structured(previewed).Deserialize<RemoteMergePreview>(JsonOptions)!;

        // The default is spelled out rather than left to be inferred, so a caller can see what it is
        // about to accept.
        RemoteMergeConflict conflict = Assert.Single(preview.Conflicts);
        Assert.Equal(birthday.Id, conflict.FieldDefinitionId);
        Assert.Equal(nameof(RecordMergeResolution.KeepSurviving), conflict.Resolution);
        Assert.Equal("1815-12-11", Assert.Single(conflict.MergedValues).Value);
        Assert.Equal(["Augusta Ada King"], preview.AliasesAdded);

        var request = new
        {
            domainId,
            survivingRecordId = keep.Record.Id,
            mergedRecordId = lose.Record.Id,
            expectedRevision = preview.Revision,
            idempotencyKey = Guid.CreateVersion7(),
            choices = new[] { new { fieldDefinitionId = birthday.Id, resolution = "TakeMerged" } },
        };
        using JsonDocument merged = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "merge_records", request);
        RecordCommandReceipt receipt = Structured(merged).Deserialize<RecordCommandReceipt>(JsonOptions)!;

        // The receipt names both records: the one that remains, so a caller can read it, and the one
        // that does not, so a replay still says what became of it.
        Assert.Equal(2, receipt.Items.Count);
        Assert.Equal("merged", receipt.Items.Single(item => item.Id == keep.Record.Id).Outcome);
        Assert.Equal("deleted", receipt.Items.Single(item => item.Id == lose.Record.Id).Outcome);

        RecordDetails after = Assert.IsType<RecordDetails>(await records.GetRecordAsync(keep.Record.Id));
        Assert.Equal("1815-12-11", Assert.Single(after.Values, value => value.FieldDefinitionId == birthday.Id).DateValue);
        Assert.Equal("555-0100", Assert.Single(after.Values, value => value.FieldDefinitionId == phone.Id).TextValue);
        Assert.Equal(["Augusta Ada King"], after.Aliases);
        Assert.Null(await records.GetRecordAsync(lose.Record.Id));

        // A merge deletes a record, so a retry that ran again would delete a second one. It replays.
        using JsonDocument replay = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "merge_records", request);
        Assert.Equal(Structured(merged).GetRawText(), Structured(replay).GetRawText());

        // What the merge did not keep is readable, which is the promise the whole feature rests on.
        using JsonDocument sources = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_record_source",
            new { domainId, recordId = keep.Record.Id });
        RemoteRecordSource retained = Structured(sources).Deserialize<RemoteRecordSource>(JsonOptions)!;
        Assert.Equal(RecordSourceKinds.Merge, Assert.Single(retained.Imports).SourceKind);
        Assert.Contains("1815-12-10", retained.Values.Select(value => value.ValuePreview));
        Assert.Contains("Augusta Ada King", retained.Values.Select(value => value.ValuePreview));

        // And a merge issued against the revision the preview handed back refuses now that the records
        // have changed, rather than merging whatever happens to be there.
        RecordDetails third = await records.CreateRecordAsync(type.Id, "Third", []);
        using JsonDocument stale = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "merge_records",
            new
            {
                domainId,
                survivingRecordId = keep.Record.Id,
                mergedRecordId = third.Record.Id,
                expectedRevision = preview.Revision,
                idempotencyKey = Guid.CreateVersion7(),
            });
        AssertWriteError(stale, "stale_revision");
        Assert.NotNull(await records.GetRecordAsync(third.Record.Id));
    }
}
