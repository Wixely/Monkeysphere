using Dapper;
using DnaX.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Data;

namespace Monkeysphere.Data.Tests;

public sealed partial class RecordWorkflowTests
{
    [Fact]
    public async Task DeletionPreviewCountsDependenciesAndCommitsCascadeReceiptAndMediaQueue()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRecordDeletionStore deletions = application.Services.GetRequiredService<IRecordDeletionStore>();
        RecordType type = await records.CreateRecordTypeAsync("Deletion fixture");
        FieldDefinition date = await records.CreateAndAttachFieldAsync(type.Id, new("Date", FieldTypes.ExactDate, false));
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Remove fixture", [new(date.Id, "2026-09-07")], ["Alias"]);
        RecordDetails other = await records.CreateRecordAsync(type.Id, "Keep fixture", []);
        _ = await application.Services.GetRequiredService<IReminderService>().CreateAsync(record.Values[0].Id, 1);
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        RelationshipType relationshipType = await relationships.CreateTypeAsync(new("Knows", RelationshipDirectionality.Symmetric));
        await relationships.CreateAsync(relationshipType.Id, record.Record.Id, other.Record.Id);
        IGraphViewService graph = application.Services.GetRequiredService<IGraphViewService>();
        GraphView view = await graph.CreateAsync(new("Keep view", RelationshipGraphDisplayMode.Connected,
            [record.Record.Id, other.Record.Id], [type.Id], [new(record.Record.Id, 0, 0), new(other.Record.Id, 200, 0)]));
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        await application.Services.GetRequiredService<IRecordImageService>().AddAsync(record.Record.Id, new MemoryStream(png), "fixture.png");
        await using (var connection = await application.Services.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                INSERT INTO VCardImports (Fingerprint, RecordId, SourceVersion, ImportedAtUtc) VALUES ('fixture', @Id, '4.0', '2026-09-07T00:00:00Z');
                INSERT INTO VCardProperties (RecordId, Ordinal, PropertyName, ParametersJson, RawValue, MappingKind)
                VALUES (@Id, 0, 'X-FIXTURE', '[]', 'opaque', 0);
                """, new { Id = record.Record.Id.ToString("D") });
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RecordDeletionPreview preview = await deletions.PreviewDeletionAsync(DeletionIdentity(), record.Record.Id, record.Revision, now);
        Assert.Equal(new(1, 1, 1, png.Length, 1, 1, 1, 1, 1, 1, 1), preview.Impact);
        Assert.NotNull(await records.GetRecordAsync(record.Record.Id));
        RecordCommandIdentity apply = DeletionIdentity();
        RecordCommandReceipt receipt = await deletions.ApplyDeletionAsync(apply, record.Record.Id, record.Revision, preview.PreviewId, now);
        Assert.Equal("deleted", Assert.Single(receipt.Items).Outcome);
        Assert.Null(await records.GetRecordAsync(record.Record.Id));
        Assert.NotNull(await records.GetRecordAsync(other.Record.Id));
        Assert.Empty(await relationships.ListForRecordAsync(other.Record.Id));
        Assert.Empty(await application.Services.GetRequiredService<IReminderService>().ListActiveAsync());
        GraphView remaining = (await graph.GetAsync(view.Id))!;
        Assert.Equal([other.Record.Id], remaining.RecordIds);
        Assert.Equal(other.Record.Id, Assert.Single(remaining.NodePositions).RecordId);
        Assert.True((await deletions.GetDeletionStatusAsync(apply, now)).MediaCleanupPending);
        string directory = application.Services.GetRequiredService<IDnaXPaths>().ResolveWritable(Path.Combine("media", "records", record.Record.Id.ToString("N")));
        Assert.True(Directory.Exists(directory));
        await application.RestartAsync();
        deletions = application.Services.GetRequiredService<IRecordDeletionStore>();
        await deletions.CleanupPendingRecordMediaAsync(now);
        Assert.False(Directory.Exists(directory));
        Assert.False((await deletions.GetDeletionStatusAsync(apply, now)).MediaCleanupPending);
        await deletions.CleanupDeletionPreviewsAsync(preview.ExpiresAtUtc);
        RecordCommandReceipt replay = await deletions.ApplyDeletionAsync(apply, record.Record.Id, record.Revision, preview.PreviewId, preview.ExpiresAtUtc.AddMinutes(1));
        Assert.Equal(receipt.Items.ToArray(), replay.Items.ToArray());
        await using var finalConnection = await application.Services.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        Assert.Equal(0, await finalConnection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM VCardProperties;"));
        Assert.Equal(0, await finalConnection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM VCardImports;"));
        Assert.Equal(1, await finalConnection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ApplicationCommandAudit WHERE Outcome = 'committed';"));
    }

    [Fact]
    public async Task DeletionImpactRejectsChangedDependenciesAndExpiredOrForeignPreviews()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRecordDeletionStore deletions = application.Services.GetRequiredService<IRecordDeletionStore>();
        RecordType type = await records.CreateRecordTypeAsync("Impact fixture");
        FieldDefinition date = await records.CreateAndAttachFieldAsync(type.Id, new("Date", FieldTypes.ExactDate, false));
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Keep", [new(date.Id, "2026-09-07")]);
        RecordDetails other = await records.CreateRecordAsync(type.Id, "Other", []);
        await application.Services.GetRequiredService<IReminderService>().CreateAsync(record.Values[0].Id, 1);
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        RelationshipType link = await relationships.CreateTypeAsync(new("Related", RelationshipDirectionality.Symmetric));
        await relationships.CreateAsync(link.Id, record.Record.Id, other.Record.Id);
        await application.Services.GetRequiredService<IGraphViewService>().CreateAsync(new("Graph", RelationshipGraphDisplayMode.Connected,
            [record.Record.Id], [type.Id], [new(record.Record.Id, 0, 0)]));
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        await application.Services.GetRequiredService<IRecordImageService>().AddAsync(record.Record.Id, new MemoryStream(png), "fixture.png");
        await using var connection = await application.Services.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        var parameter = new { Id = record.Record.Id.ToString("D") };
        await connection.ExecuteAsync("""
            INSERT INTO VCardImports (Fingerprint, RecordId, SourceVersion, ImportedAtUtc) VALUES ('fixture', @Id, '4.0', '2026-09-07T00:00:00Z');
            INSERT INTO VCardProperties (RecordId, Ordinal, PropertyName, ParametersJson, RawValue, MappingKind)
            VALUES (@Id, 0, 'X-FIXTURE', '[]', 'opaque', 0);
            """, parameter);
        string[] edits = [
            "UPDATE RecordImages SET Caption = 'Changed' WHERE RecordId = @Id;",
            "UPDATE Relationships SET Note = 'Changed' WHERE SourceRecordId = @Id OR TargetRecordId = @Id;",
            "UPDATE Reminders SET LeadDays = 2 WHERE RecordId = @Id;",
            "UPDATE GraphViewRecords SET SortOrder = 1 WHERE RecordId = @Id;",
            "UPDATE GraphViewNodePositions SET X = 100 WHERE RecordId = @Id;",
            "UPDATE VCardImports SET SourceVersion = '3.0' WHERE RecordId = @Id;",
            "UPDATE VCardProperties SET RawValue = 'Changed' WHERE RecordId = @Id;",
        ];
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (string edit in edits)
        {
            RecordDeletionPreview preview = await deletions.PreviewDeletionAsync(DeletionIdentity(), record.Record.Id, record.Revision, now);
            await connection.ExecuteAsync(edit, parameter);
            Assert.Equal(record.Revision, (await records.GetRecordAsync(record.Record.Id))!.Revision);
            RecordPreviewException stale = await Assert.ThrowsAsync<RecordPreviewException>(() =>
                deletions.ApplyDeletionAsync(DeletionIdentity(), record.Record.Id, record.Revision, preview.PreviewId, now));
            Assert.Equal("stale_preview", stale.Code);
        }
        RecordCommandIdentity owner = DeletionIdentity();
        RecordDeletionPreview fresh = await deletions.PreviewDeletionAsync(owner, record.Record.Id, record.Revision, now);
        await Assert.ThrowsAsync<RecordCommandNotFoundException>(() => deletions.GetDeletionPreviewAsync(
            owner with { CredentialFingerprint = new string('C', 64) }, fresh.PreviewId, now));
        RecordPreviewException expired = await Assert.ThrowsAsync<RecordPreviewException>(() =>
            deletions.ApplyDeletionAsync(DeletionIdentity(), record.Record.Id, record.Revision, fresh.PreviewId, fresh.ExpiresAtUtc));
        Assert.Equal("preview_expired", expired.Code);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            deletions.ApplyDeletionAsync(DeletionIdentity(), other.Record.Id, record.Revision, fresh.PreviewId, now));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RecordCommandReceipts;"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RecordMediaCleanup;"));
        await records.UpdateRecordAsync(record.Record.Id, "Changed content", [new(date.Id, "2026-09-07")], expectedRevision: record.Revision);
        RecordPreviewException changedContent = await Assert.ThrowsAsync<RecordPreviewException>(() =>
            deletions.ApplyDeletionAsync(DeletionIdentity(), record.Record.Id, record.Revision, fresh.PreviewId, now));
        Assert.Equal("stale_preview", changedContent.Code);
    }

    [Fact]
    public async Task MediaCleanupFailurePreservesCommittedReceiptAndRetriesWithoutTouchingLiveRecords()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRecordDeletionStore deletions = application.Services.GetRequiredService<IRecordDeletionStore>();
        RecordType type = await records.CreateRecordTypeAsync("Cleanup retry fixture");
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Delete fixture", []);
        string directory = application.Services.GetRequiredService<IDnaXPaths>().ResolveWritable(Path.Combine("media", "records", record.Record.Id.ToString("N")));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "fixture.original.png");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RecordDeletionPreview preview = await deletions.PreviewDeletionAsync(DeletionIdentity(), record.Record.Id, record.Revision, now);
        RecordCommandIdentity apply = DeletionIdentity();
        await deletions.ApplyDeletionAsync(apply, record.Record.Id, record.Revision, preview.PreviewId, now);
        if (OperatingSystem.IsWindows())
        {
            using FileStream locked = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.False(await deletions.TryCleanupRecordMediaAsync(record.Record.Id, now));
            Assert.True((await deletions.GetDeletionStatusAsync(apply, now)).MediaCleanupPending);
            Assert.Null(await records.GetRecordAsync(record.Record.Id));
        }
        Assert.True(await deletions.TryCleanupRecordMediaAsync(record.Record.Id, now));
        Assert.False(Directory.Exists(directory));
        Assert.False((await deletions.GetDeletionStatusAsync(apply, now)).MediaCleanupPending);
        RecordDetails live = await records.CreateRecordAsync(type.Id, "Keep live media", []);
        string liveDirectory = application.Services.GetRequiredService<IDnaXPaths>().ResolveWritable(Path.Combine("media", "records", live.Record.Id.ToString("N")));
        Directory.CreateDirectory(liveDirectory);
        await using var connection = await application.Services.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        await connection.ExecuteAsync("INSERT INTO RecordMediaCleanup (RecordId) VALUES (@Id);", new { Id = live.Record.Id.ToString("D") });
        Assert.False(await deletions.TryCleanupRecordMediaAsync(live.Record.Id, now));
        Assert.True(Directory.Exists(liveDirectory));
        Assert.NotNull(await records.GetRecordAsync(live.Record.Id));
    }

    [Fact]
    public async Task DeletionQueueQuotaRejectsBeforeDatabaseMutation()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRecordDeletionStore deletions = application.Services.GetRequiredService<IRecordDeletionStore>();
        RecordType type = await records.CreateRecordTypeAsync("Queue quota fixture");
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Keep", []);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RecordDeletionPreview preview = await deletions.PreviewDeletionAsync(DeletionIdentity(), record.Record.Id, record.Revision, now);
        await using var connection = await application.Services.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        await connection.ExecuteAsync("""
            WITH RECURSIVE queue(Value) AS (SELECT 1 UNION ALL SELECT Value + 1 FROM queue WHERE Value < @Maximum)
            INSERT INTO RecordMediaCleanup (RecordId) SELECT printf('00000000-0000-7000-8000-%012d', Value) FROM queue;
            """, new { Maximum = RecordCommandLimits.MaximumPendingMediaCleanupPerDomain });
        RecordPreviewException full = await Assert.ThrowsAsync<RecordPreviewException>(() =>
            deletions.ApplyDeletionAsync(DeletionIdentity(), record.Record.Id, record.Revision, preview.PreviewId, now));
        Assert.Equal("limit_exceeded", full.Code);
        Assert.NotNull(await records.GetRecordAsync(record.Record.Id));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RecordCommandReceipts;"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT Applied FROM RecordDeletionPreviews;"));
        await deletions.CleanupPendingRecordMediaAsync(now);
        Assert.Equal(900, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RecordMediaCleanup;"));
    }

    [Fact]
    public async Task DeletionKeepsCleanupQueuedUntilAnInFlightImageUploadFinishes()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRecordImageService images = application.Services.GetRequiredService<IRecordImageService>();
        RecordDeletionService deletions = application.Services.GetRequiredService<RecordDeletionService>();
        IRecordDeletionStore store = application.Services.GetRequiredService<IRecordDeletionStore>();
        RecordType type = await records.CreateRecordTypeAsync("Upload race fixture");
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Remove", []);
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        await using PausedImageStream upload = new(png);
        Task<RecordImage> adding = images.AddAsync(record.Record.Id, upload, "fixture.png");
        await upload.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        RecordCommandIdentity apply = DeletionIdentity();
        try
        {
            RecordDeletionStatus status = await deletions.DeleteAsync(apply, record.Record.Id, record.Revision, null);
            Assert.True(status.MediaCleanupPending);
            Assert.Null(await records.GetRecordAsync(record.Record.Id));
        }
        finally
        {
            upload.Resume.TrySetResult();
        }
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(async () => await adding);
        Assert.True(await store.TryCleanupRecordMediaAsync(record.Record.Id, DateTimeOffset.UtcNow));
        Assert.False((await deletions.GetStatusAsync(apply)).MediaCleanupPending);
        string directory = application.Services.GetRequiredService<IDnaXPaths>().ResolveWritable(Path.Combine("media", "records", record.Record.Id.ToString("N")));
        Assert.False(Directory.Exists(directory));
    }

    private sealed class PausedImageStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private static RecordCommandIdentity DeletionIdentity() => new(MonkeysphereDomains.DefaultId, "mcp", new string('A', 64),
        "records.delete", Guid.CreateVersion7(), new string('B', 64));
}
