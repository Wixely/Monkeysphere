using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Data;

namespace Monkeysphere.Data.Tests;

public sealed partial class RecordWorkflowTests
{
    [Fact]
    public async Task BatchPreviewSurvivesRestartAndApplyReceiptOutlivesPreview()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordBatchService batches = application.Services.GetRequiredService<RecordBatchService>();
        RecordType type = await records.CreateRecordTypeAsync("Restart batch fixture");
        FieldDefinition temporal = await records.CreateAndAttachFieldAsync(type.Id, new("Period", FieldTypes.Temporal, false));
        RecordCommandIdentity previewIdentity = BatchIdentity();
        RecordBatchPreview preview = await batches.PreviewAsync(previewIdentity,
            [new("create", type.Id, "Persisted preview", [new(temporal.Id, Temporal: new("2010", TemporalPrecision.Decade, true, "Estimate"))])]);
        await application.RestartAsync();
        batches = application.Services.GetRequiredService<RecordBatchService>();
        IRecordBatchStore store = application.Services.GetRequiredService<IRecordBatchStore>();
        RecordBatchPreview restored = await batches.GetAsync(previewIdentity, preview.PreviewId);
        Assert.Equal(preview.PreviewId, restored.PreviewId);
        Assert.Equal(preview.Items.ToArray(), restored.Items.ToArray());
        RecordCommandIdentity applyIdentity = BatchIdentity();
        RecordCommandReceipt applied = await batches.ApplyAsync(applyIdentity, preview.PreviewId);
        Assert.Equal(preview.Items[0].Id, Assert.Single(applied.Items).Id);
        RecordDetails record = (await application.Services.GetRequiredService<IMonkeysphereService>().GetRecordAsync(preview.Items[0].Id))!;
        Assert.Equal(TemporalPrecision.Decade, Assert.Single(record.Values).TemporalPrecision);
        Assert.Equal("Estimate", record.Values[0].ApproximationNote);
        Assert.True((await batches.GetAsync(previewIdentity, preview.PreviewId)).Applied);
        RecordPreviewException consumed = await Assert.ThrowsAsync<RecordPreviewException>(() => batches.ApplyAsync(BatchIdentity(), preview.PreviewId));
        Assert.Equal("preview_consumed", consumed.Code);
        await store.CleanupExpiredPreviewsAsync(preview.ExpiresAtUtc);
        RecordCommandReceipt replay = await store.ApplyPreviewAsync(applyIdentity, preview.PreviewId, preview.ExpiresAtUtc.AddMinutes(1));
        Assert.Equal(applied.Items.ToArray(), replay.Items.ToArray());
        Assert.Equal(applied.CompletedAtUtc, replay.CompletedAtUtc);
        await application.RestartAsync();
        RecordCommandReceipt restarted = await application.Services.GetRequiredService<RecordBatchService>().ApplyAsync(applyIdentity, preview.PreviewId);
        Assert.Equal(applied.Items.ToArray(), restarted.Items.ToArray());
        await application.Services.GetRequiredService<IDebugDatabaseResetService>().ResetAsync();
        await using var connection = await application.Services.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RecordBatchPreviews;"));
    }

    [Fact]
    public async Task BatchPreviewRejectsExpirySchemaChangesAndRepeatedTargets()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordBatchService batches = application.Services.GetRequiredService<RecordBatchService>();
        IRecordBatchStore store = application.Services.GetRequiredService<IRecordBatchStore>();
        RecordType type = await records.CreateRecordTypeAsync("Expiry fixture");
        RecordCommandIdentity owner = BatchIdentity();
        RecordBatchPreview preview = await batches.PreviewAsync(owner, [new("create", type.Id, "Pending", [])]);
        RecordPreviewException expired = await Assert.ThrowsAsync<RecordPreviewException>(() => store.ApplyPreviewAsync(BatchIdentity(), preview.PreviewId, preview.ExpiresAtUtc));
        Assert.Equal("preview_expired", expired.Code);
        await Assert.ThrowsAsync<RecordCommandNotFoundException>(() => batches.GetAsync(owner with { CredentialFingerprint = new string('C', 64) }, preview.PreviewId));
        await records.CreateAndAttachFieldAsync(type.Id, new("New required field", FieldTypes.Text, true));
        RecordPreviewException stale = await Assert.ThrowsAsync<RecordPreviewException>(() => batches.ApplyAsync(BatchIdentity(), preview.PreviewId));
        Assert.Equal("stale_preview", stale.Code);
        Assert.Equal(0, (await records.SearchRecordsAsync(new())).TotalCount);

        RecordType emptyType = await records.CreateRecordTypeAsync("Duplicate target fixture");
        RecordDetails original = await records.CreateRecordAsync(emptyType.Id, "Original", []);
        RecordBatchInput patch = new("patch", Id: original.Record.Id, ExpectedRevision: original.Revision, Changes: [new("set_name", Value: "New")]);
        await Assert.ThrowsAsync<DomainValidationException>(() => batches.PreviewAsync(BatchIdentity(), [patch, patch]));
        Assert.Equal(original.Revision, (await records.GetRecordAsync(original.Record.Id))!.Revision);
    }

    [Fact]
    public async Task BatchPreviewEnforcesStorageQuotaAndReclaimsExpiredRows()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordCommandService commands = application.Services.GetRequiredService<RecordCommandService>();
        IRecordBatchStore store = application.Services.GetRequiredService<IRecordBatchStore>();
        RecordType type = await records.CreateRecordTypeAsync("Quota fixture");
        PreparedRecord prepared = await commands.PrepareCreateAsync(type.Id, "Quota fixture", []);
        Guid recordId = Guid.CreateVersion7();
        PreparedRecordMutation[] mutations = [new(RecordMutationKind.Create, recordId, prepared)];
        RecordBatchPreviewItem[] items = [new(0, "create", recordId, type.Id, "Quota fixture", 0, 0)];
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int index = 0; index < RecordCommandLimits.MaximumRetainedPreviewsPerDomain; index++)
            await store.SavePreviewAsync(BatchIdentity(), mutations, items, now);
        RecordPreviewException full = await Assert.ThrowsAsync<RecordPreviewException>(() => store.SavePreviewAsync(BatchIdentity(), mutations, items, now));
        Assert.Equal("limit_exceeded", full.Code);
        DateTimeOffset afterExpiry = now.AddMinutes(RecordCommandLimits.PreviewLifetimeMinutes);
        _ = await store.SavePreviewAsync(BatchIdentity(), mutations, items, afterExpiry);
        await using var connection = await application.Services.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RecordBatchPreviews;"));
        PreparedRecord oversized = prepared with { Aliases = [new string('x', RecordCommandLimits.MaximumPreviewBytes)] };
        RecordPreviewException large = await Assert.ThrowsAsync<RecordPreviewException>(() => store.SavePreviewAsync(BatchIdentity(),
            [new(RecordMutationKind.Create, recordId, oversized)], items, afterExpiry));
        Assert.Equal("limit_exceeded", large.Code);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RecordBatchPreviews;"));
        Assert.Equal(0, (await records.SearchRecordsAsync(new())).TotalCount);
    }

    private static RecordCommandIdentity BatchIdentity() => new(MonkeysphereDomains.DefaultId, "mcp", new string('A', 64),
        "records.batch", Guid.CreateVersion7(), new string('B', 64));
}
