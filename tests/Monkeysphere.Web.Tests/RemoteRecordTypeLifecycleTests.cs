using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// Renaming, retiring and merging a record type were browser-only, so a remote caller could create
/// structure and never tidy it up. These pin the three properties that make the remote path the same
/// act rather than a second one: it shares the page's SQL, so records, saved views, reminders and
/// import provenance survive a merge exactly as they do in the browser; it checks the same usage
/// fingerprint, so a preview overtaken by other work is refused; and it reports that refusal as
/// <c>stale_revision</c> rather than a validation failure, because retrying the identical call can
/// never succeed.
/// </summary>
public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task McpMergesRecordTypesAndKeepsRecordsViewsRemindersAndProvenance()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Guid domainId = MonkeysphereDomains.DefaultId;

        Guid sourceId;
        Guid targetId;
        Guid adaId;
        Guid viewId;
        Guid reminderId;
        Guid enrichedFieldId;
        Guid unknownFieldId;
        Guid sharedFieldId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordType source = await records.CreateRecordTypeAsync("Merged colleague");
            RecordType target = await records.CreateRecordTypeAsync("Merged person");
            sourceId = source.Id;
            targetId = target.Id;

            // Shared on both sides and required only on the target, with the source holding records:
            // the merge has to relax it, or records that were valid a moment ago stop being valid.
            FieldDefinition shared = await records.CreateAndAttachFieldAsync(source.Id, new("Shared city", FieldTypes.Text, false));
            sharedFieldId = shared.Id;
            await records.AttachFieldAsync(target.Id, shared.Id, isRequired: true);

            // Import provenance on the source side, which the merge moves rather than rewrites.
            FieldDefinition enriched = await records.CreateEnrichmentFieldAsync(
                source.Id, "Organisation", FieldTypes.Text, "monkeysphere.person.organisation");
            enrichedFieldId = enriched.Id;

            // A field type this build does not recognize, kept as text. Nothing about a merge should
            // decide it now understands the value.
            FieldDefinition unknown = await records.CreateAndAttachFieldAsync(source.Id, new("Sigil", "some-future-type", false));
            unknownFieldId = unknown.Id;

            FieldDefinition birthday = await records.CreateAndAttachFieldAsync(source.Id, new("Birthday", FieldTypes.ExactDate, false));
            await records.SetFieldRecurrenceAsync(birthday.Id, new(true, 1));

            RecordDetails ada = await records.CreateRecordAsync(source.Id, "Ada", [
                new(shared.Id, "London"),
                new(enriched.Id, "Countess"),
                new(unknown.Id, "✡ three stars"),
                new(birthday.Id, "1815-12-10"),
            ]);
            adaId = ada.Record.Id;

            SavedViewDetails view = await scope.ServiceProvider.GetRequiredService<ISavedViewService>()
                .CreateAsync(new("Colleagues in London", source.Id, null, [shared.Id],
                    [new(shared.Id, FieldFilterOperator.Equals, "London")]));
            viewId = view.View.Id;

            reminderId = (await scope.ServiceProvider.GetRequiredService<IReminderService>()
                .CreateAsync(ada.Values.Single(value => value.FieldDefinitionId == birthday.Id).Id, 7)).Id;
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read", "structure.write"]);

        using JsonDocument previewed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "preview_record_type_merge", new { domainId, sourceRecordTypeId = sourceId, targetRecordTypeId = targetId });
        RemoteRecordTypeMergePreview preview = Structured(previewed).Deserialize<RemoteRecordTypeMergePreview>(JsonOptions)!;

        // Reported against the same preview the page draws, so the numbers an operator would read
        // before clicking are the numbers a client reads before calling.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            RecordTypeMergePreview page = await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>()
                .PreviewRecordTypeMergeAsync(sourceId, targetId);
            Assert.Equal(page.Revision, preview.ExpectedUsageRevision);
            Assert.Equal(page.SourceRecordCount, preview.SourceRecordCount);
            Assert.Equal(page.SharedFieldCount, preview.SharedFieldCount);
            Assert.Equal(page.AddedFieldCount, preview.AddedFieldCount);
            Assert.Equal(page.RequiredDowngradeCount, preview.RequiredDowngradeCount);
        }

        Assert.Equal(1, preview.SourceRecordCount);
        Assert.Equal(0, preview.TargetRecordCount);
        Assert.Equal(1, preview.SourceSavedViewCount);
        Assert.Equal(1, preview.SharedFieldCount);
        Assert.Equal(3, preview.AddedFieldCount);
        Assert.Equal(1, preview.RequiredDowngradeCount);
        Assert.Equal("active", preview.Source.Lifecycle);

        using JsonDocument merged = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "merge_record_types",
            new
            {
                domainId,
                sourceRecordTypeId = sourceId,
                targetRecordTypeId = targetId,
                expectedUsageRevision = preview.ExpectedUsageRevision,
                idempotencyKey = Guid.CreateVersion7(),
            });
        RecordCommandReceipt receipt = Structured(merged).Deserialize<RecordCommandReceipt>(JsonOptions)!;

        // The source first and named retired, because that is the half of a merge a caller is most
        // likely to have not expected.
        Assert.Equal([sourceId, targetId], receipt.Items.Select(item => item.Id));
        Assert.Equal("retired", receipt.Items[0].Outcome);
        Assert.Equal("updated", receipt.Items[1].Outcome);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordDetails ada = (await records.GetRecordAsync(adaId))!;
            Assert.Equal(targetId, ada.Record.RecordTypeId);

            // Every value came across, the unrecognized type still holding exactly the text it was
            // given rather than anything a merge decided it meant.
            Assert.Equal("London", ada.Values.Single(value => value.FieldDefinitionId == sharedFieldId).TextValue);
            Assert.Equal("Countess", ada.Values.Single(value => value.FieldDefinitionId == enrichedFieldId).TextValue);
            Assert.Equal("✡ three stars", ada.Values.Single(value => value.FieldDefinitionId == unknownFieldId).TextValue);

            RecordTypeDetails target = (await records.GetRecordTypeAsync(targetId))!;

            // The required field on the target side was relaxed rather than left to reject Ada, who
            // never had a value for it.
            Assert.False(target.Fields.Single(field => field.Definition.Id == sharedFieldId).IsRequired);

            // The imported field kept the canonical key that says where it came from, so a later
            // import still recognizes it instead of making a second one.
            Assert.Equal("monkeysphere.person.organisation",
                target.Fields.Single(field => field.Definition.Id == enrichedFieldId).Definition.CanonicalKey);

            Assert.Equal(RecordTypeLifecycle.Retired, (await records.GetRecordTypeAsync(sourceId))!.RecordType.Lifecycle);

            // The saved view follows its records rather than pointing at a retired type.
            SavedViewDetails view = (await scope.ServiceProvider.GetRequiredService<ISavedViewService>().GetAsync(viewId))!;
            Assert.Equal(targetId, view.View.RecordTypeId);

            // And the reminder is still armed for the date it was set on: it names a value, and the
            // value's record moving type is not a reason to lose it.
            ReminderItem item = Assert.Single(await scope.ServiceProvider.GetRequiredService<IReminderService>().ListActiveAsync());
            Assert.Equal(reminderId, item.Reminder.Id);
            Assert.Equal(adaId, item.Entry.RecordId);

            // Reported against the type it now belongs to, so a reminder list does not name a
            // retired type at a record that has moved on.
            Assert.Equal(targetId, item.Entry.RecordTypeId);
        }
    }

    [Fact]
    public async Task McpRefusesAMergeOrRetirementWhosePreviewHasBeenOvertaken()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Guid domainId = MonkeysphereDomains.DefaultId;

        Guid sourceId;
        Guid targetId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            sourceId = (await records.CreateRecordTypeAsync("Stale source")).Id;
            targetId = (await records.CreateRecordTypeAsync("Stale target")).Id;
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read", "structure.write"]);

        using JsonDocument previewedMerge = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "preview_record_type_merge", new { domainId, sourceRecordTypeId = sourceId, targetRecordTypeId = targetId });
        string mergeRevision = Structured(previewedMerge).Deserialize<RemoteRecordTypeMergePreview>(JsonOptions)!.ExpectedUsageRevision;

        using JsonDocument previewedRetirement = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "preview_record_type_retirement", new { domainId, recordTypeId = sourceId });
        RemoteRecordTypeRetirementPreview retirement = Structured(previewedRetirement)
            .Deserialize<RemoteRecordTypeRetirementPreview>(JsonOptions)!;
        Assert.Equal(0, retirement.RecordCount);
        Assert.Equal(0, retirement.SavedViewCount);

        // Somebody adds a record to the source between the preview and the call, which is exactly the
        // case the fingerprint exists for: what the preview counted is no longer what would move.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            _ = await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>()
                .CreateRecordAsync(sourceId, "Arrived late", []);
        }

        using JsonDocument staleMerge = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "merge_record_types",
            new { domainId, sourceRecordTypeId = sourceId, targetRecordTypeId = targetId, expectedUsageRevision = mergeRevision, idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(staleMerge, "stale_revision");

        using JsonDocument staleRetirement = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "retire_record_type",
            new { domainId, recordTypeId = sourceId, expectedUsageRevision = retirement.ExpectedUsageRevision, idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(staleRetirement, "stale_revision");

        // Nothing happened, so the refusal cost the caller only a second preview.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            Assert.Equal(RecordTypeLifecycle.Active, (await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>()
                .GetRecordTypeAsync(sourceId))!.RecordType.Lifecycle);
        }

        using JsonDocument again = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "preview_record_type_retirement", new { domainId, recordTypeId = sourceId });
        RemoteRecordTypeRetirementPreview fresh = Structured(again).Deserialize<RemoteRecordTypeRetirementPreview>(JsonOptions)!;
        Assert.Equal(1, fresh.RecordCount);

        using JsonDocument retired = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "retire_record_type",
            new { domainId, recordTypeId = sourceId, expectedUsageRevision = fresh.ExpectedUsageRevision, idempotencyKey = Guid.CreateVersion7() });
        RecordMutationOutcome outcome = Assert.Single(Structured(retired).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items);
        Assert.Equal("retired", outcome.Outcome);

        // Retirement stops a type being offered; it does not take the record with it.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordTypeDetails source = (await records.GetRecordTypeAsync(sourceId))!;
            Assert.Equal(RecordTypeLifecycle.Retired, source.RecordType.Lifecycle);
            Assert.Equal(1, (await records.SearchRecordsAsync(new RecordSearch(RecordTypeId: sourceId))).TotalCount);
        }

        // Merging a type into itself is refused at the preview, which is the earliest point it can be:
        // a caller never gets a fingerprint for it, so there is nothing to carry to the apply. Asserted
        // here rather than on the apply because a stale fingerprint would make that pass for the wrong
        // reason and prove nothing.
        using JsonDocument selfPreview = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "preview_record_type_merge", new { domainId, sourceRecordTypeId = targetId, targetRecordTypeId = targetId });
        AssertWriteError(selfPreview, "validation_failed");

        // And refused again at the apply, in case a caller invents a fingerprint rather than reading
        // one. It cannot be a matching one, so this can only report the staleness it finds first.
        using JsonDocument itself = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "merge_record_types",
            new { domainId, sourceRecordTypeId = targetId, targetRecordTypeId = targetId, expectedUsageRevision = mergeRevision, idempotencyKey = Guid.CreateVersion7() });
        Assert.True(itself.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            Assert.Equal(RecordTypeLifecycle.Active, (await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>()
                .GetRecordTypeAsync(targetId))!.RecordType.Lifecycle);
        }
    }

    [Fact]
    public async Task McpRenamesARecordTypeAndKeepsTheTagsItWasToldToTurnOff()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Guid domainId = MonkeysphereDomains.DefaultId;

        Guid typeId;
        string revision;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            typeId = (await records.CreateRecordTypeAsync("Renamed thing")).Id;
            _ = await records.CreateRecordAsync(typeId, "Tagged", [], null, ["keep-me"]);
            revision = (await records.GetRecordTypeAsync(typeId))!.RecordType.Revision;
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read", "structure.write"]);
        Guid key = Guid.CreateVersion7();

        using JsonDocument updated = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "update_record_type",
            new { domainId, recordTypeId = typeId, expectedRevision = revision, name = "  Renamed properly  ", symbol = "\U0001F5FF", tagsEnabled = false, idempotencyKey = key });
        RecordMutationOutcome outcome = Assert.Single(Structured(updated).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items);
        Assert.Equal("updated", outcome.Outcome);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            RecordType type = (await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>().GetRecordTypeAsync(typeId))!.RecordType;

            // Trimmed by the same rule the page uses, so a name saved remotely is a name the page
            // would have saved.
            Assert.Equal("Renamed properly", type.Name);
            Assert.Equal("\U0001F5FF", type.Symbol);
            Assert.False(type.TagsEnabled);
            Assert.NotEqual(revision, type.Revision);
        }

        // The identical retry replays rather than writing again, and the stale revision it still
        // carries is not consulted, which is what makes a lost response safe to retry.
        using JsonDocument replayed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "update_record_type",
            new { domainId, recordTypeId = typeId, expectedRevision = revision, name = "  Renamed properly  ", symbol = "\U0001F5FF", tagsEnabled = false, idempotencyKey = key });
        Assert.Equal(outcome.Revision, Assert.Single(Structured(replayed).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items).Revision);

        // A revision from before the rename is refused as stale rather than silently overwriting it.
        using JsonDocument stale = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "update_record_type",
            new { domainId, recordTypeId = typeId, expectedRevision = revision, name = "Renamed again", idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(stale, "stale_revision");

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordTypeDetails type = (await records.GetRecordTypeAsync(typeId))!;

            // Turning tags off hides them; it does not throw them away. Turning them back on has to
            // restore what the type had, which is why the rows are still there to restore.
            using JsonDocument reEnabled = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "update_record_type",
                new { domainId, recordTypeId = typeId, expectedRevision = type.RecordType.Revision, name = "Renamed properly", symbol = "\U0001F5FF", tagsEnabled = true, idempotencyKey = Guid.CreateVersion7() });
            _ = Structured(reEnabled);

            PagedResult<RecordSummary> tagged = await records.SearchRecordsAsync(new RecordSearch(RecordTypeId: typeId, Tags: ["keep-me"]));
            Assert.Equal(1, tagged.TotalCount);
        }
    }

    [Theory]
    [InlineData("structure.write", true)]
    [InlineData("records.write", false)]
    public async Task RecordTypeLifecycleToolsNeedStructureWriteToPreviewAsWellAsToApply(string grant, bool allowed)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Guid domainId = MonkeysphereDomains.DefaultId;

        Guid typeId;
        string revision;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            typeId = (await records.CreateRecordTypeAsync("Guarded type")).Id;
            revision = (await records.GetRecordTypeAsync(typeId))!.RecordType.Revision;
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read", grant]);

        // The preview is guarded too. It counts nothing a reader could not count, but the fingerprint
        // it returns is only usable by somebody who can retire, so the workflow sits behind one grant.
        using JsonDocument previewed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "preview_record_type_retirement", new { domainId, recordTypeId = typeId });
        if (!allowed) AssertWriteError(previewed, "permission_denied");

        using JsonDocument updated = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "update_record_type",
            new { domainId, recordTypeId = typeId, expectedRevision = revision, name = "Renamed by a grant", idempotencyKey = Guid.CreateVersion7() });
        if (allowed) _ = Structured(updated); else AssertWriteError(updated, "permission_denied");

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            Assert.Equal(allowed ? "Renamed by a grant" : "Guarded type",
                (await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>().GetRecordTypeAsync(typeId))!.RecordType.Name);
        }
    }
}
