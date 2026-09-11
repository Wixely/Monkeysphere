using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed partial class SqliteMonkeysphereStore : IRecordDeletionStore
{
    public async Task<RecordDeletionPreview> PreviewDeletionAsync(RecordCommandIdentity identity, Guid id, string expectedRevision,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateDeletionIdentity(identity);
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        DeletionPreviewRow? prior = await connection.QuerySingleOrDefaultAsync<DeletionPreviewRow>(new CommandDefinition("""
            SELECT RequestHash, SummaryJson, ExpiresAtUtc, Applied FROM RecordDeletionPreviews
            WHERE Surface = @Surface AND CredentialFingerprint = @Fingerprint AND IdempotencyKey = @Key;
            """, new { identity.Surface, Fingerprint = identity.CredentialFingerprint.ToUpperInvariant(), Key = Key(identity.IdempotencyKey) },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (prior is not null)
        {
            if (!string.Equals(prior.RequestHash, identity.RequestHash, StringComparison.OrdinalIgnoreCase))
                throw new CommandReplayException("retry_conflict", "The preview retry key was used with a different request.");
            return ReadDeletionPreview(prior, now);
        }
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(expectedRevision))
            throw new DomainValidationException("Deletion preview requires a record ID and its expected revision.");
        DeletionRecordRow record = await connection.QuerySingleOrDefaultAsync<DeletionRecordRow>(new CommandDefinition(
            "SELECT Revision, DeletionRevision FROM Records WHERE Id = @Id;", new { Id = Key(id) }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false) ?? throw new RecordCommandNotFoundException("Record was not found.");
        if (record.Revision != expectedRevision) throw new ConcurrencyConflictException("The record changed. Read it again before previewing deletion.");
        RecordDeletionImpact impact = await connection.QuerySingleAsync<RecordDeletionImpact>(new CommandDefinition("""
            SELECT
                (SELECT COUNT(*) FROM FieldValues WHERE RecordId = @Id) AS FieldValueCount,
                (SELECT COUNT(*) FROM RecordAliases WHERE RecordId = @Id) AS AliasCount,
                (SELECT COUNT(*) FROM RecordImages WHERE RecordId = @Id) AS ImageCount,
                (SELECT COALESCE(SUM(OriginalByteLength), 0) FROM RecordImages WHERE RecordId = @Id) AS OriginalImageBytes,
                (SELECT COUNT(*) FROM Relationships WHERE SourceRecordId = @Id OR TargetRecordId = @Id) AS RelationshipCount,
                (SELECT COUNT(*) FROM Reminders WHERE RecordId = @Id) AS ReminderCount,
                (SELECT COUNT(*) FROM GraphViewRecords WHERE RecordId = @Id) AS GraphSelectionCount,
                (SELECT COUNT(*) FROM GraphViewNodePositions WHERE RecordId = @Id) AS GraphPositionCount,
                (SELECT COUNT(*) FROM (SELECT GraphViewId FROM GraphViewRecords WHERE RecordId = @Id
                    UNION SELECT GraphViewId FROM GraphViewNodePositions WHERE RecordId = @Id)) AS AffectedGraphViewCount,
                (SELECT COUNT(*) FROM RecordSourceImports WHERE RecordId = @Id) AS ImportFingerprintCount,
                (SELECT COUNT(*) FROM RecordSourceValues WHERE RecordId = @Id) AS ImportedPropertyCount;
            """, new { Id = Key(id) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        RecordDeletionPreview preview = new(Guid.CreateVersion7(), id, record.Revision, record.DeletionRevision, impact,
            now.ToUniversalTime(), now.ToUniversalTime().AddMinutes(RecordCommandLimits.PreviewLifetimeMinutes));
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM RecordDeletionPreviews WHERE ExpiresAtUtc <= @Now;
            DELETE FROM RecordBatchPreviews WHERE ExpiresAtUtc <= @Now;
            """, new { Now = Timestamp(now) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        int count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT (SELECT COUNT(*) FROM RecordDeletionPreviews) + (SELECT COUNT(*) FROM RecordBatchPreviews);",
            transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (count >= RecordCommandLimits.MaximumRetainedPreviewsPerDomain)
            throw new RecordPreviewException("limit_exceeded", "The domain preview quota is full. Wait for old previews to expire.");
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO RecordDeletionPreviews (Id, Surface, CredentialFingerprint, IdempotencyKey, RequestHash, SummaryJson, ExpiresAtUtc)
            VALUES (@Id, @Surface, @Fingerprint, @Key, @Hash, @SummaryJson, @ExpiresAt);
            """, new
        {
            Id = Key(preview.PreviewId),
            identity.Surface,
            Fingerprint = identity.CredentialFingerprint.ToUpperInvariant(),
            Key = Key(identity.IdempotencyKey),
            Hash = identity.RequestHash.ToUpperInvariant(),
            SummaryJson = JsonSerializer.Serialize(preview),
            ExpiresAt = Timestamp(preview.ExpiresAtUtc)
        }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return preview;
    }

    public async Task<RecordDeletionPreview> GetDeletionPreviewAsync(RecordCommandIdentity identity, Guid previewId,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateDeletionIdentity(identity);
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return ReadDeletionPreview(await GetDeletionPreviewRowAsync(connection, null, identity, previewId, cancellationToken).ConfigureAwait(false), now);
    }

    public async Task<RecordCommandReceipt> ApplyDeletionAsync(RecordCommandIdentity identity, Guid id, string expectedRevision, Guid? previewId,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateDeletionIdentity(identity);
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        RecordCommandReceipt? replay = await ReadReceiptAsync(connection, transaction, identity, now, cancellationToken).ConfigureAwait(false);
        if (replay is not null) return replay;
        if (previewId is Guid selectedPreview)
        {
            RecordDeletionPreview preview = ReadDeletionPreview(
                await GetDeletionPreviewRowAsync(connection, transaction, identity, selectedPreview, cancellationToken).ConfigureAwait(false), now);
            if (preview.Applied) throw new RecordPreviewException("preview_consumed", "This deletion preview was applied. Use the original retry key to retrieve its receipt.");
            if (preview.RecordId != id || preview.RecordRevision != expectedRevision)
                throw new DomainValidationException("The record and revision must match the reviewed deletion preview.");
            string? revision = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT DeletionRevision FROM Records WHERE Id = @Id;", new { Id = Key(id) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (revision != preview.ImpactRevision)
                throw new RecordPreviewException("stale_preview", "The record or its dependent data changed after preview. Generate and review a new deletion preview.");
        }
        else
        {
            await RequireRevisionAsync(connection, transaction, id, expectedRevision, schema: false, cancellationToken).ConfigureAwait(false);
            bool hasDependencies = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(SELECT 1 FROM FieldValues WHERE RecordId = @Id)
                    OR EXISTS(SELECT 1 FROM RecordAliases WHERE RecordId = @Id)
                    OR EXISTS(SELECT 1 FROM RecordImages WHERE RecordId = @Id)
                    OR EXISTS(SELECT 1 FROM Relationships WHERE SourceRecordId = @Id OR TargetRecordId = @Id)
                    OR EXISTS(SELECT 1 FROM Reminders WHERE RecordId = @Id)
                    OR EXISTS(SELECT 1 FROM GraphViewRecords WHERE RecordId = @Id)
                    OR EXISTS(SELECT 1 FROM GraphViewNodePositions WHERE RecordId = @Id)
                    OR EXISTS(SELECT 1 FROM RecordSourceImports WHERE RecordId = @Id)
                    OR EXISTS(SELECT 1 FROM RecordSourceValues WHERE RecordId = @Id);
                """, new { Id = Key(id) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (hasDependencies) throw new DomainValidationException("This record has dependent data. Review a deletion preview and supply its previewId.");
        }
        await RequireCommandCapacityAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);
        _ = await DeleteRecordCoreAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        RecordCommandReceipt receipt = await SaveCommandReceiptAsync(connection, transaction, identity,
            [new(id, expectedRevision, "deleted")], now, cancellationToken).ConfigureAwait(false);
        if (previewId is Guid consumedPreview)
            await connection.ExecuteAsync(new CommandDefinition("UPDATE RecordDeletionPreviews SET Applied = 1 WHERE Id = @Id;",
                new { Id = Key(consumedPreview) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    public async Task<RecordDeletionStatus> GetDeletionStatusAsync(RecordCommandIdentity identity, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateDeletionIdentity(identity);
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Status is addressed by the owning credential and apply key; no original request payload is required.
        CommandReceiptRow row = await connection.QuerySingleOrDefaultAsync<CommandReceiptRow>(new CommandDefinition("""
            SELECT RequestHash, ReceiptJson, RetryUntilUtc FROM RecordCommandReceipts
            WHERE Surface = @Surface AND CredentialFingerprint = @Fingerprint AND Action = 'records.delete' AND IdempotencyKey = @Key;
            """, new { identity.Surface, Fingerprint = identity.CredentialFingerprint.ToUpperInvariant(), Key = Key(identity.IdempotencyKey) },
            cancellationToken: cancellationToken)).ConfigureAwait(false) ?? throw new RecordCommandNotFoundException("The deletion receipt was not found for this credential and domain.");
        if (row.ReceiptJson is null || ParseTimestamp(row.RetryUntilUtc) <= now)
            throw new CommandReplayException("retry_expired", "The deletion receipt has expired.");
        RecordCommandReceipt receipt = JsonSerializer.Deserialize<RecordCommandReceipt>(row.ReceiptJson)
            ?? throw new InvalidOperationException("The deletion receipt could not be read.");
        bool pending = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM RecordMediaCleanup WHERE RecordId = @Id);",
            new { Id = Key(receipt.Items.Single().Id) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new(receipt, pending);
    }

    public async Task CleanupDeletionPreviewsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM RecordDeletionPreviews WHERE ExpiresAtUtc <= @Now;",
            new { Now = Timestamp(now) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private void ValidateDeletionIdentity(RecordCommandIdentity identity)
    {
        ValidateIdentity(identity);
        if (identity.Action != "records.delete") throw new DomainValidationException("The deletion action is invalid.");
    }

    private static async Task<DeletionPreviewRow> GetDeletionPreviewRowAsync(SqliteConnection connection, SqliteTransaction? transaction,
        RecordCommandIdentity identity, Guid previewId, CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<DeletionPreviewRow>(new CommandDefinition("""
            SELECT RequestHash, SummaryJson, ExpiresAtUtc, Applied FROM RecordDeletionPreviews
            WHERE Id = @Id AND Surface = @Surface AND CredentialFingerprint = @Fingerprint;
            """, new { Id = Key(previewId), identity.Surface, Fingerprint = identity.CredentialFingerprint.ToUpperInvariant() },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)
        ?? throw new RecordCommandNotFoundException("The deletion preview was not found for this credential and domain.");

    private static RecordDeletionPreview ReadDeletionPreview(DeletionPreviewRow row, DateTimeOffset now)
    {
        if (ParseTimestamp(row.ExpiresAtUtc) <= now) throw new RecordPreviewException("preview_expired", "The deletion preview expired. Generate and review a new preview.");
        RecordDeletionPreview preview = JsonSerializer.Deserialize<RecordDeletionPreview>(row.SummaryJson)
            ?? throw new InvalidOperationException("The deletion preview could not be read.");
        return preview with { Applied = row.Applied };
    }

    private sealed class DeletionPreviewRow
    {
        public required string RequestHash { get; init; }
        public required string SummaryJson { get; init; }
        public required string ExpiresAtUtc { get; init; }
        public bool Applied { get; init; }
    }

    private sealed class DeletionRecordRow
    {
        public required string Revision { get; init; }
        public required string DeletionRevision { get; init; }
    }
}
