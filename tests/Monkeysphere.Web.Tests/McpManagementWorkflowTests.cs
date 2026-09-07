using System.Text.Json;
using DnaX.RemoteAccess;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task McpOnlyManagementWorksInTwoDomainsAndReadOnlyCredentialCannotRepeatWrites()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory,
            ["domains.manage", "records.read", "structure.write", "records.write", "relationships.write", "records.delete"]);
        List<(string Tool, object Request)> writes = [];
        async Task<JsonElement> CallAsync(string tool, object request, bool write = false)
        {
            using JsonDocument result = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", tool, request);
            Assert.False(result.RootElement.GetProperty("result").TryGetProperty("isError", out JsonElement error) && error.GetBoolean(), result.RootElement.GetRawText());
            if (write) writes.Add((tool, request));
            return Structured(result).Clone();
        }
        async Task<RecordCommandReceipt> WriteAsync(string tool, object request) =>
            (await CallAsync(tool, request, write: true)).Deserialize<RecordCommandReceipt>(JsonOptions)!;

        Guid[] domainIds = [Guid.NewGuid(), Guid.NewGuid()];
        foreach (Guid domainId in domainIds)
            _ = await WriteAsync("create_domain", new { domainId, name = $"Workflow {Array.IndexOf(domainIds, domainId) + 1}", idempotencyKey = Guid.NewGuid() });
        List<Guid> survivingIds = [];
        string[] firstAliases = ["First alias"];
        string[] secondAliases = ["Second alias"];
        foreach (Guid domainId in domainIds)
        {
            RemoteSetupState state = (await CallAsync("get_setup_state", new { domainId })).Deserialize<RemoteSetupState>(JsonOptions)!;
            Assert.False(state.Setup.IsComplete);
            _ = await WriteAsync("complete_setup", new
            {
                domainId,
                starterPackKey = "blank",
                selectedPresetKeys = Array.Empty<string>(),
                expectedRevision = state.Revision,
                expectedCatalogRevision = state.CatalogRevision,
                acknowledgeBlank = true,
                idempotencyKey = Guid.NewGuid()
            });
            RecordMutationOutcome type = (await WriteAsync("create_record_type", new { domainId, name = "People", idempotencyKey = Guid.NewGuid() })).Items[0];
            RecordCommandReceipt number = await WriteAsync("create_and_attach_field", new
            {
                domainId,
                recordTypeId = type.Id,
                expectedRevision = type.Revision,
                name = "Score",
                typeId = "number",
                idempotencyKey = Guid.NewGuid()
            });
            RecordCommandReceipt tags = await WriteAsync("create_and_attach_field", new
            {
                domainId,
                recordTypeId = type.Id,
                expectedRevision = number.Items[1].Revision,
                name = "Tags",
                typeId = "tags",
                idempotencyKey = Guid.NewGuid()
            });
            RecordMutationOutcome first = (await WriteAsync("create_record", new
            {
                domainId,
                recordTypeId = type.Id,
                displayName = "Alpha",
                aliases = firstAliases,
                values = new[] { new RemoteFieldInput(number.Items[0].Id, "10"), new RemoteFieldInput(tags.Items[0].Id, Tags: ["Friend", "Group"]) },
                idempotencyKey = Guid.NewGuid()
            })).Items[0];
            RecordMutationOutcome second = (await WriteAsync("create_record", new
            {
                domainId,
                recordTypeId = type.Id,
                displayName = "Beta",
                aliases = secondAliases,
                values = new[] { new RemoteFieldInput(number.Items[0].Id, "20"), new RemoteFieldInput(tags.Items[0].Id, Tags: ["Friend"]) },
                idempotencyKey = Guid.NewGuid()
            })).Items[0];
            RecordMutationOutcome relationshipType = (await WriteAsync("create_relationship_type", new { domainId, name = "Knows", directionality = "symmetric", idempotencyKey = Guid.NewGuid() })).Items[0];
            _ = await WriteAsync("create_relationship", new
            {
                domainId,
                typeId = relationshipType.Id,
                sourceRecordId = first.Id,
                targetRecordId = second.Id,
                expectedTypeRevision = relationshipType.Revision,
                expectedSourceRevision = first.Revision,
                expectedTargetRevision = second.Revision,
                idempotencyKey = Guid.NewGuid()
            });
            RemoteRecord current = (await CallAsync("get_record", new { domainId, id = first.Id })).Deserialize<RemoteRecord>(JsonOptions)!;
            Assert.Single(current.Relationships);
            _ = await WriteAsync("patch_record", new
            {
                domainId,
                id = first.Id,
                expectedRevision = current.Revision,
                changes = new[] { new RemoteRecordPatchChange("set_name", Value: "Edited Alpha"), new RemoteRecordPatchChange("set_field", number.Items[0].Id, ScalarValue: "15") },
                idempotencyKey = Guid.NewGuid()
            });
            RemotePage<RemoteRecordSummary> found = (await CallAsync("query_records", new
            {
                domainId,
                query = "First alias",
                recordTypeId = type.Id,
                filters = new[] { new RemoteRecordFilter(number.Items[0].Id, "greater_than", "12"), new RemoteRecordFilter(tags.Items[0].Id, "equals", "Friend") },
                sort = new RemoteRecordSort(number.Items[0].Id, true)
            })).Deserialize<RemotePage<RemoteRecordSummary>>(JsonOptions)!;
            Assert.Equal(first.Id, Assert.Single(found.Items).Id);
            Assert.Equal("Edited Alpha", found.Items[0].DisplayName);
            Guid otherDomain = domainIds.Single(id => id != domainId);
            using (JsonDocument foreign = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_record", new { domainId = otherDomain, id = first.Id }))
                Assert.Equal(0, foreign.RootElement.GetProperty("result").GetProperty("content").GetArrayLength());
            using (JsonDocument foreignFilter = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_records",
                new { domainId = otherDomain, filters = new[] { new RemoteRecordFilter(number.Items[0].Id, "greater_than", "0") } }))
                Assert.True(foreignFilter.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
            current = (await CallAsync("get_record", new { domainId, id = first.Id })).Deserialize<RemoteRecord>(JsonOptions)!;
            RecordDeletionPreview preview = (await CallAsync("preview_record_deletion", new
            {
                domainId,
                id = first.Id,
                expectedRevision = current.Revision,
                idempotencyKey = Guid.NewGuid()
            }, write: true)).Deserialize<RecordDeletionPreview>(JsonOptions)!;
            Assert.Equal(1, preview.Impact.RelationshipCount);
            Assert.Equal(1, preview.Impact.AliasCount);
            _ = await CallAsync("delete_record", new { domainId, id = first.Id, expectedRevision = current.Revision, previewId = preview.PreviewId, idempotencyKey = Guid.NewGuid() }, write: true);
            RemoteRecord survivor = (await CallAsync("get_record", new { domainId, id = second.Id })).Deserialize<RemoteRecord>(JsonOptions)!;
            Assert.Empty(survivor.Relationships);
            Assert.Equal("Beta", survivor.Record.DisplayName);
            Assert.Equal(["Second alias"], survivor.Aliases);
            Assert.Equal(2, survivor.Values.Count);
            survivingIds.Add(second.Id);
        }
        RemoteCredentialManager manager = factory.Services.GetRequiredService<RemoteCredentialManager>();
        DnaXGeneratedCredential readOnly = await manager.RotateAsync(DnaXRemoteSurface.Mcp, surface.Version, ["records.read"]);
        foreach ((string tool, object request) in writes)
        {
            using JsonDocument denied = await SendAsync(client, surface.EndpointPath!, readOnly.Secret, "tools/call", tool, request);
            AssertWriteError(denied, "permission_denied");
        }
        for (int index = 0; index < domainIds.Length; index++)
        {
            using JsonDocument result = await SendAsync(client, surface.EndpointPath!, readOnly.Secret, "tools/call", "query_records", new { domainId = domainIds[index] });
            RemotePage<RemoteRecordSummary> page = Structured(result).Deserialize<RemotePage<RemoteRecordSummary>>(JsonOptions)!;
            Assert.Equal(1, page.TotalCount);
            Assert.Equal(survivingIds[index], Assert.Single(page.Items).Id);
        }
    }
}
