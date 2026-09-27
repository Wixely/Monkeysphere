using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// A reusable field could be created remotely and then only tidied up in the browser. These pin the
/// four things that make the remote lifecycle the same lifecycle: a merge moves everything the page's
/// merge moves and the conflict policy decides which value — and which reminder — survives; a
/// conversion that cannot carry a value is refused rather than quietly dropping it; a preview overtaken
/// by other work is refused as <c>stale_revision</c>; and none of it becomes a way to read records,
/// because <c>structure.write</c> is not <c>records.read</c>.
/// </summary>
public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task McpMergesFieldsUnderTheChosenConflictPolicyAndMovesEveryReference()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Guid domainId = MonkeysphereDomains.DefaultId;

        Guid typeId;
        Guid sourceId;
        Guid targetId;
        Guid adaId;
        Guid graceId;
        Guid viewId;
        Guid survivingReminder;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordType person = await records.CreateRecordTypeAsync("Merged field person");
            typeId = person.Id;

            // Required on the source side only: the merged attachment has to end up required, because
            // the stricter of the two is the one that was being relied on.
            FieldDefinition source = await records.CreateAndAttachFieldAsync(person.Id, new("Birthday", FieldTypes.ExactDate, true));
            FieldDefinition target = await records.CreateAndAttachFieldAsync(person.Id, new("Date of birth", FieldTypes.ExactDate, false));
            sourceId = source.Id;
            targetId = target.Id;
            // Both, because a field's recurrence is part of its configuration and a merge requires the
            // configurations to match: merging a repeating date into a one-off one would quietly change
            // what every moved value means.
            await records.SetFieldRecurrenceAsync(source.Id, new(true, 1));
            await records.SetFieldRecurrenceAsync(target.Id, new(true, 1));

            RecordDetails ada = await records.CreateRecordAsync(person.Id, "Ada", [new(source.Id, "1815-12-10")]);
            adaId = ada.Record.Id;
            RecordDetails grace = await records.CreateRecordAsync(person.Id, "Grace",
                [new(source.Id, "1906-12-09"), new(target.Id, "1906-12-08")]);
            graceId = grace.Record.Id;

            survivingReminder = (await scope.ServiceProvider.GetRequiredService<IReminderService>()
                .CreateAsync(grace.Values.Single(value => value.FieldDefinitionId == target.Id).Id, 7)).Id;

            SavedViewDetails view = await scope.ServiceProvider.GetRequiredService<ISavedViewService>()
                .CreateAsync(new("Birthdays", person.Id, null, [source.Id],
                    [new(source.Id, FieldFilterOperator.Equals, "1815-12-10")], source.Id, source.Id));
            viewId = view.View.Id;
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read", "structure.write"]);

        using JsonDocument usageResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "get_field_usage", new { domainId, fieldDefinitionId = sourceId });
        RemoteFieldUsage usage = Structured(usageResult).Deserialize<RemoteFieldUsage>(JsonOptions)!;
        Assert.Equal(1, usage.AttachmentCount);
        Assert.Equal(2, usage.ValueCount);

        // Four references: a column, a filter, the grouping and the sort all name the same field.
        Assert.Equal(4, usage.SavedViewReferenceCount);
        Assert.Equal("Birthday", usage.Definition.Name);

        using JsonDocument previewed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "preview_field_merge", new { domainId, sourceFieldDefinitionId = sourceId, targetFieldDefinitionId = targetId });
        RemoteFieldMergePreview preview = Structured(previewed).Deserialize<RemoteFieldMergePreview>(JsonOptions)!;
        Assert.True(preview.IsCompatible);
        Assert.Null(preview.IncompatibilityReason);
        Assert.Equal(2, preview.SourceValueCount);
        Assert.Equal(1, preview.ConflictingValueCount);

        // Not the same fingerprint, and deliberately: get_field_usage covers one field, while a merge's
        // covers the pair, because either side moving is a reason to look again. A caller carrying the
        // wrong one gets stale_revision, so the two are worth keeping visibly distinct.
        Assert.NotEqual(usage.ExpectedUsageRevision, preview.ExpectedUsageRevision);

        // Grace holds both, so a merge must be told which value to keep. Refusing is the default.
        using JsonDocument rejected = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "merge_fields",
            new { domainId, sourceFieldDefinitionId = sourceId, targetFieldDefinitionId = targetId, conflictResolution = "reject", expectedUsageRevision = preview.ExpectedUsageRevision, idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(rejected, "validation_failed");

        using JsonDocument nonsense = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "merge_fields",
            new { domainId, sourceFieldDefinitionId = sourceId, targetFieldDefinitionId = targetId, conflictResolution = "keepWhicheverYouLike", expectedUsageRevision = preview.ExpectedUsageRevision, idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(nonsense, "validation_failed");

        using JsonDocument merged = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "merge_fields",
            new { domainId, sourceFieldDefinitionId = sourceId, targetFieldDefinitionId = targetId, conflictResolution = "keepTarget", expectedUsageRevision = preview.ExpectedUsageRevision, idempotencyKey = Guid.CreateVersion7() });
        RecordCommandReceipt receipt = Structured(merged).Deserialize<RecordCommandReceipt>(JsonOptions)!;
        Assert.Equal([sourceId, targetId], receipt.Items.Select(item => item.Id));
        Assert.Equal("retired", receipt.Items[0].Outcome);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();

            // Ada had only the source value, so it came across untouched under the surviving field.
            RecordValue ada = Assert.Single((await records.GetRecordAsync(adaId))!.Values);
            Assert.Equal(targetId, ada.FieldDefinitionId);
            Assert.Equal("1815-12-10", ada.DateValue);

            // Grace held both and the policy said keep the target, so the target's day is the one left.
            RecordValue grace = Assert.Single((await records.GetRecordAsync(graceId))!.Values);
            Assert.Equal("1906-12-08", grace.DateValue);

            RecordTypeDetails type = (await records.GetRecordTypeAsync(typeId))!;
            RecordTypeField attachment = Assert.Single(type.Fields, field => field.Definition.Id == targetId);
            Assert.True(attachment.IsRequired);
            Assert.DoesNotContain(type.Fields, field => field.Definition.Id == sourceId);
            Assert.Equal(FieldLifecycle.Retired,
                Assert.Single(await records.ListFieldDefinitionsAsync(), field => field.Id == sourceId).Lifecycle);

            // Every saved-view reference follows the field rather than pointing at a retired one.
            SavedViewDetails view = (await scope.ServiceProvider.GetRequiredService<ISavedViewService>().GetAsync(viewId))!;
            Assert.Equal([targetId], view.ColumnFieldDefinitionIds);
            Assert.All(view.Filters, filter => Assert.Equal(targetId, filter.FieldDefinitionId));
            Assert.Equal(targetId, view.View.GroupByFieldDefinitionId);
            Assert.Equal(targetId, view.View.SortFieldDefinitionId);

            // And the reminder on the value that survived is still armed for its next occurrence.
            ReminderItem item = Assert.Single(await scope.ServiceProvider.GetRequiredService<IReminderService>().ListActiveAsync());
            Assert.Equal(survivingReminder, item.Reminder.Id);
            Assert.Equal(targetId, item.Entry.FieldDefinitionId);
        }
    }

    [Fact]
    public async Task McpRefusesAConversionThatWouldLoseAValueAndCarriesTheRestAcross()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Guid domainId = MonkeysphereDomains.DefaultId;

        Guid typeId;
        Guid fieldId;
        Guid numericId;
        Guid wordyId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordType thing = await records.CreateRecordTypeAsync("Converted thing");
            typeId = thing.Id;
            FieldDefinition field = await records.CreateAndAttachFieldAsync(thing.Id, new("Size", FieldTypes.Text, false));
            fieldId = field.Id;
            numericId = (await records.CreateRecordAsync(thing.Id, "Numeric", [new(field.Id, "42")])).Record.Id;
            wordyId = (await records.CreateRecordAsync(thing.Id, "Wordy", [new(field.Id, "quite large")])).Record.Id;
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read", "structure.write"]);

        using JsonDocument unsafePreview = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "preview_field_conversion", new { domainId, fieldDefinitionId = fieldId, name = "Size", typeId = FieldTypes.Number });
        RemoteFieldConversionPreview unsafeConversion = Structured(unsafePreview).Deserialize<RemoteFieldConversionPreview>(JsonOptions)!;

        // "quite large" is not a number, so the preview says so before anything is written, and names
        // the record holding it because this credential can read records anyway.
        Assert.Equal(1, unsafeConversion.FailedValueCount);
        Assert.False(unsafeConversion.IssueRecordsWithheld);
        RemoteFieldConversionIssue issue = Assert.Single(unsafeConversion.Issues);
        Assert.Equal(wordyId, issue.RecordId);
        Assert.Equal("Wordy", issue.RecordDisplayName);

        // And the conversion is refused rather than carrying the one it can and losing the other.
        using JsonDocument refused = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "convert_field",
            new { domainId, fieldDefinitionId = fieldId, expectedUsageRevision = unsafeConversion.ExpectedUsageRevision, name = "Size", typeId = FieldTypes.Number, idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(refused, "validation_failed");

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            Assert.Equal(FieldLifecycle.Active,
                Assert.Single(await records.ListFieldDefinitionsAsync(), field => field.Id == fieldId).Lifecycle);
            Assert.Equal("quite large", Assert.Single((await records.GetRecordAsync(wordyId))!.Values).TextValue);

            // The offending value is corrected, which is what the preview was for.
            _ = await records.UpdateRecordAsync(wordyId, "Wordy", [new(fieldId, "100")]);
        }

        using JsonDocument safePreview = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "preview_field_conversion", new { domainId, fieldDefinitionId = fieldId, name = "  Size in units  ", typeId = FieldTypes.Number });
        RemoteFieldConversionPreview safeConversion = Structured(safePreview).Deserialize<RemoteFieldConversionPreview>(JsonOptions)!;
        Assert.Equal(0, safeConversion.FailedValueCount);

        // A conversion's fingerprint covers the one field, so get_field_usage reports the same one and
        // is a way to check a preview is still good without recomputing the conversion.
        using JsonDocument usageResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "get_field_usage", new { domainId, fieldDefinitionId = fieldId });
        Assert.Equal(safeConversion.ExpectedUsageRevision,
            Structured(usageResult).Deserialize<RemoteFieldUsage>(JsonOptions)!.ExpectedUsageRevision);
        Assert.Empty(safeConversion.Issues);
        Assert.Equal("Size in units", safeConversion.TargetName);

        using JsonDocument converted = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "convert_field",
            new { domainId, fieldDefinitionId = fieldId, expectedUsageRevision = safeConversion.ExpectedUsageRevision, name = "  Size in units  ", typeId = FieldTypes.Number, idempotencyKey = Guid.CreateVersion7() });
        RecordCommandReceipt receipt = Structured(converted).Deserialize<RecordCommandReceipt>(JsonOptions)!;

        // The new field first, because its identifier is the one the caller has no other way of learning.
        Assert.Equal("created", receipt.Items[0].Outcome);
        Assert.Equal("retired", receipt.Items[1].Outcome);
        Assert.Equal(fieldId, receipt.Items[1].Id);
        Guid convertedId = receipt.Items[0].Id;

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            FieldDefinition created = Assert.Single(await records.ListFieldDefinitionsAsync(), field => field.Id == convertedId);
            Assert.Equal(FieldTypes.Number, created.TypeId);
            Assert.Equal("Size in units", created.Name);

            // Both values came across as numbers, under the new field, on their own records.
            Assert.Equal(convertedId, Assert.Single((await records.GetRecordAsync(numericId))!.Values).FieldDefinitionId);
            Assert.Equal("42", Assert.Single((await records.GetRecordAsync(numericId))!.Values).NumberValue);
            Assert.Equal("100", Assert.Single((await records.GetRecordAsync(wordyId))!.Values).NumberValue);

            // The attachment moved with it, so the record type carries the new field and not the old.
            RecordTypeDetails type = (await records.GetRecordTypeAsync(typeId))!;
            Assert.Equal(convertedId, Assert.Single(type.Fields).Definition.Id);
        }
    }

    [Fact]
    public async Task McpRenamesAndRetiresAFieldAndRefusesAStalePreview()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Guid domainId = MonkeysphereDomains.DefaultId;

        Guid typeId;
        Guid fieldId;
        Guid otherId;
        string fieldRevision;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordType thing = await records.CreateRecordTypeAsync("Renamed field thing");
            typeId = thing.Id;
            FieldDefinition field = await records.CreateAndAttachFieldAsync(thing.Id, new("Colour", FieldTypes.Text, false));
            fieldId = field.Id;
            otherId = (await records.CreateAndAttachFieldAsync(thing.Id, new("Color", FieldTypes.Text, false))).Id;
            _ = await records.CreateRecordAsync(thing.Id, "Kept", [new(field.Id, "green")]);
            fieldRevision = Assert.Single(await records.ListFieldDefinitionsAsync(), definition => definition.Id == fieldId).Revision;
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read", "structure.write"]);
        Guid key = Guid.CreateVersion7();

        using JsonDocument renamed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_field",
            new { domainId, fieldDefinitionId = fieldId, expectedFieldRevision = fieldRevision, name = "  Colour of it  ", idempotencyKey = key });
        RecordMutationOutcome outcome = Assert.Single(Structured(renamed).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items);

        // The identical retry replays rather than renaming again, carrying the same now-stale revision.
        using JsonDocument replayed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_field",
            new { domainId, fieldDefinitionId = fieldId, expectedFieldRevision = fieldRevision, name = "  Colour of it  ", idempotencyKey = key });
        Assert.Equal(outcome.Revision, Assert.Single(Structured(replayed).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items).Revision);

        using JsonDocument stale = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_field",
            new { domainId, fieldDefinitionId = fieldId, expectedFieldRevision = fieldRevision, name = "Something else", idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(stale, "stale_revision");

        // A merge preview overtaken by a new value is refused, and refused as staleness rather than as
        // something a corrected retry could fix.
        using JsonDocument previewed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "preview_field_merge", new { domainId, sourceFieldDefinitionId = otherId, targetFieldDefinitionId = fieldId });
        string mergeRevision = Structured(previewed).Deserialize<RemoteFieldMergePreview>(JsonOptions)!.ExpectedUsageRevision;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            _ = await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>()
                .CreateRecordAsync(typeId, "Arrived late", [new(otherId, "blue")]);
        }

        using JsonDocument staleMerge = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "merge_fields",
            new { domainId, sourceFieldDefinitionId = otherId, targetFieldDefinitionId = fieldId, conflictResolution = "keepTarget", expectedUsageRevision = mergeRevision, idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(staleMerge, "stale_revision");

        string current;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            FieldDefinition field = Assert.Single(
                await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>().ListFieldDefinitionsAsync(),
                definition => definition.Id == fieldId);
            Assert.Equal("Colour of it", field.Name);
            current = field.Revision;
        }

        using JsonDocument retired = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "retire_field",
            new { domainId, fieldDefinitionId = fieldId, expectedFieldRevision = current, idempotencyKey = Guid.CreateVersion7() });
        Assert.Equal("retired", Assert.Single(Structured(retired).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items).Outcome);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            FieldDefinition field = Assert.Single(await records.ListFieldDefinitionsAsync(), definition => definition.Id == fieldId);
            Assert.Equal(FieldLifecycle.Retired, field.Lifecycle);

            // Retiring keeps what was already recorded, which is the whole reason it is not a deletion.
            Assert.Equal(1, (await records.GetFieldUsageCountsAsync(fieldId))!.ValueCount);

            // And a retired field can still be renamed, because its values are still shown somewhere.
            using JsonDocument again = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_field",
                new { domainId, fieldDefinitionId = fieldId, expectedFieldRevision = field.Revision, name = "Old colour", idempotencyKey = Guid.CreateVersion7() });
            _ = Structured(again);
        }

        // Retiring it twice is refused rather than reported as done.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            FieldDefinition field = Assert.Single(
                await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>().ListFieldDefinitionsAsync(),
                definition => definition.Id == fieldId);
            using JsonDocument twice = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "retire_field",
                new { domainId, fieldDefinitionId = fieldId, expectedFieldRevision = field.Revision, idempotencyKey = Guid.CreateVersion7() });
            AssertWriteError(twice, "validation_failed");
        }
    }

    [Theory]
    [InlineData("structure.write", true)]
    [InlineData("records.write", false)]
    public async Task FieldLifecycleToolsNeedStructureWriteAndDoNotBecomeAWayToReadRecords(string grant, bool allowed)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Guid domainId = MonkeysphereDomains.DefaultId;

        Guid fieldId;
        string revision;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordType thing = await records.CreateRecordTypeAsync("Guarded field thing");
            FieldDefinition field = await records.CreateAndAttachFieldAsync(thing.Id, new("Size", FieldTypes.Text, false));
            fieldId = field.Id;
            _ = await records.CreateRecordAsync(thing.Id, "Secret person", [new(field.Id, "quite large")]);
            revision = field.Revision;
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);

        using JsonDocument usage = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "get_field_usage", new { domainId, fieldDefinitionId = fieldId });
        if (!allowed) AssertWriteError(usage, "permission_denied");
        else
        {
            // Granted, and still no record content: a count of values, never the values or their records.
            RemoteFieldUsage counted = Structured(usage).Deserialize<RemoteFieldUsage>(JsonOptions)!;
            Assert.Equal(1, counted.ValueCount);
            Assert.DoesNotContain("Secret person", Structured(usage).GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("quite large", Structured(usage).GetRawText(), StringComparison.Ordinal);
        }

        using JsonDocument conversion = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "preview_field_conversion", new { domainId, fieldDefinitionId = fieldId, name = "Size", typeId = FieldTypes.Number });
        if (!allowed) AssertWriteError(conversion, "permission_denied");
        else
        {
            // structure.write alone learns that one value cannot convert, and not whose it is. That is
            // the whole reason the record is named separately from the count being reported.
            RemoteFieldConversionPreview preview = Structured(conversion).Deserialize<RemoteFieldConversionPreview>(JsonOptions)!;
            Assert.Equal(1, preview.FailedValueCount);
            Assert.True(preview.IssueRecordsWithheld);
            RemoteFieldConversionIssue issue = Assert.Single(preview.Issues);
            Assert.Null(issue.RecordId);
            Assert.Equal("(withheld)", issue.RecordDisplayName);
            Assert.DoesNotContain("Secret person", Structured(conversion).GetRawText(), StringComparison.Ordinal);
        }

        using JsonDocument renamed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_field",
            new { domainId, fieldDefinitionId = fieldId, expectedFieldRevision = revision, name = "Renamed by a grant", idempotencyKey = Guid.CreateVersion7() });
        if (allowed) _ = Structured(renamed); else AssertWriteError(renamed, "permission_denied");

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            Assert.Equal(allowed ? "Renamed by a grant" : "Size", Assert.Single(
                await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>().ListFieldDefinitionsAsync(),
                definition => definition.Id == fieldId).Name);
        }
    }
}
