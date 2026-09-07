using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed partial class SqliteMonkeysphereStore : IRecordBatchStore
{
    public async Task CleanupExpiredPreviewsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM RecordBatchPreviews WHERE ExpiresAtUtc <= @Now;",
            new { Now = Timestamp(now) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<RecordBatchPreview?> FindPreviewAsync(RecordCommandIdentity identity, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateBatchIdentity(identity);
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await FindPreviewCoreAsync(connection, null, identity, now, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RecordBatchPreview> SavePreviewAsync(RecordCommandIdentity identity, IReadOnlyList<PreparedRecordMutation> mutations,
        IReadOnlyList<RecordBatchPreviewItem> items, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateBatchIdentity(identity);
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        RecordBatchPreview? replay = await FindPreviewCoreAsync(connection, transaction, identity, now, cancellationToken).ConfigureAwait(false);
        if (replay is not null) return replay;
        if (mutations.Count is < 1 or > RecordCommandLimits.MaximumBatchRecords || items.Count != mutations.Count ||
            mutations.Select(mutation => mutation.Id).Distinct().Count() != mutations.Count)
            throw new DomainValidationException("A preview requires 1-100 distinct record operations.");
        for (int index = 0; index < mutations.Count; index++)
        {
            PreparedRecordMutation mutation = mutations[index];
            if (mutation.Kind is not (RecordMutationKind.Create or RecordMutationKind.Replace) || mutation.Id == Guid.Empty || items[index].Id != mutation.Id)
                throw new DomainValidationException("Invalid record preview operation.");
            await RequireRevisionAsync(connection, transaction,
                mutation.Kind == RecordMutationKind.Create ? mutation.Record.RecordTypeId : mutation.Id,
                mutation.Kind == RecordMutationKind.Create ? mutation.Record.SchemaRevision : mutation.ExpectedRevision ?? "",
                schema: mutation.Kind == RecordMutationKind.Create, cancellationToken).ConfigureAwait(false);
        }
        RecordBatchPreview preview = new(Guid.CreateVersion7(), now.ToUniversalTime(),
            now.ToUniversalTime().AddMinutes(RecordCommandLimits.PreviewLifetimeMinutes), items);
        string mutationsJson = JsonSerializer.Serialize(mutations);
        string summaryJson = JsonSerializer.Serialize(preview);
        if (Encoding.UTF8.GetByteCount(mutationsJson) + Encoding.UTF8.GetByteCount(summaryJson) > RecordCommandLimits.MaximumPreviewBytes)
            throw new RecordPreviewException("limit_exceeded", "The prepared preview exceeds 1 MiB. Split the batch into smaller requests.");
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM RecordBatchPreviews WHERE ExpiresAtUtc <= @Now; DELETE FROM RecordDeletionPreviews WHERE ExpiresAtUtc <= @Now;",
            new { Now = Timestamp(now) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        int count = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT (SELECT COUNT(*) FROM RecordBatchPreviews) + (SELECT COUNT(*) FROM RecordDeletionPreviews);",
            transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (count >= RecordCommandLimits.MaximumRetainedPreviewsPerDomain)
            throw new RecordPreviewException("limit_exceeded", "The domain preview quota is full. Wait for old previews to expire.");
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO RecordBatchPreviews (Id, Surface, CredentialFingerprint, IdempotencyKey, RequestHash,
                MutationsJson, SummaryJson, CreatedAtUtc, ExpiresAtUtc)
            VALUES (@Id, @Surface, @Fingerprint, @Key, @Hash, @MutationsJson, @SummaryJson, @CreatedAt, @ExpiresAt);
            """, new
        {
            Id = Key(preview.PreviewId),
            identity.Surface,
            Fingerprint = identity.CredentialFingerprint.ToUpperInvariant(),
            Key = Key(identity.IdempotencyKey),
            Hash = identity.RequestHash.ToUpperInvariant(),
            MutationsJson = mutationsJson,
            SummaryJson = summaryJson,
            CreatedAt = Timestamp(preview.CreatedAtUtc),
            ExpiresAt = Timestamp(preview.ExpiresAtUtc),
        }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return preview;
    }

    public async Task<RecordBatchPreview> GetPreviewAsync(RecordCommandIdentity identity, Guid previewId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateBatchIdentity(identity);
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        BatchPreviewRow row = await GetPreviewRowAsync(connection, null, identity, previewId, cancellationToken).ConfigureAwait(false);
        return ReadPreview(row, now);
    }

    public async Task<RecordCommandReceipt> ApplyPreviewAsync(RecordCommandIdentity identity, Guid previewId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateBatchIdentity(identity);
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        RecordCommandReceipt? replay = await ReadReceiptAsync(connection, transaction, identity, now, cancellationToken).ConfigureAwait(false);
        if (replay is not null) return replay;
        BatchPreviewRow row = await GetPreviewRowAsync(connection, transaction, identity, previewId, cancellationToken).ConfigureAwait(false);
        RecordBatchPreview preview = ReadPreview(row, now);
        if (preview.Applied) throw new RecordPreviewException("preview_consumed", "This preview was already applied. Use the original apply retry key to retrieve its receipt.");
        PreparedRecordMutation[] mutations = JsonSerializer.Deserialize<PreparedRecordMutation[]>(row.MutationsJson)
            ?? throw new InvalidOperationException("The stored record preview could not be read.");
        RecordCommandReceipt receipt;
        try
        {
            receipt = await ExecuteCommandCoreAsync(connection, transaction, identity, mutations, now, cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            throw new RecordPreviewException("stale_preview", "A record or schema changed after preview. Generate and review a new preview.");
        }
        await connection.ExecuteAsync(new CommandDefinition("UPDATE RecordBatchPreviews SET Applied = 1, MutationsJson = '[]' WHERE Id = @Id;",
            new { Id = Key(previewId) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    private void ValidateBatchIdentity(RecordCommandIdentity identity)
    {
        ValidateIdentity(identity);
        if (identity.Action != "records.batch") throw new DomainValidationException("The batch action is invalid.");
    }

    private static async Task<RecordBatchPreview?> FindPreviewCoreAsync(SqliteConnection connection, SqliteTransaction? transaction,
        RecordCommandIdentity identity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        BatchPreviewRow? row = await connection.QuerySingleOrDefaultAsync<BatchPreviewRow>(new CommandDefinition("""
            SELECT RequestHash, MutationsJson, SummaryJson, ExpiresAtUtc, Applied FROM RecordBatchPreviews
            WHERE Surface = @Surface AND CredentialFingerprint = @Fingerprint AND IdempotencyKey = @Key;
            """, new { identity.Surface, Fingerprint = identity.CredentialFingerprint.ToUpperInvariant(), Key = Key(identity.IdempotencyKey) },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null) return null;
        if (!string.Equals(row.RequestHash, identity.RequestHash, StringComparison.OrdinalIgnoreCase))
            throw new CommandReplayException("retry_conflict", "The preview retry key was used with a different request.");
        return ReadPreview(row, now);
    }

    private static async Task<BatchPreviewRow> GetPreviewRowAsync(SqliteConnection connection, SqliteTransaction? transaction,
        RecordCommandIdentity identity, Guid previewId, CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<BatchPreviewRow>(new CommandDefinition("""
            SELECT RequestHash, MutationsJson, SummaryJson, ExpiresAtUtc, Applied FROM RecordBatchPreviews
            WHERE Id = @Id AND Surface = @Surface AND CredentialFingerprint = @Fingerprint;
            """, new { Id = Key(previewId), identity.Surface, Fingerprint = identity.CredentialFingerprint.ToUpperInvariant() },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)
        ?? throw new RecordCommandNotFoundException("The preview was not found for this credential and domain.");

    private static RecordBatchPreview ReadPreview(BatchPreviewRow row, DateTimeOffset now)
    {
        if (ParseTimestamp(row.ExpiresAtUtc) <= now) throw new RecordPreviewException("preview_expired", "The preview expired. Generate and review a new preview.");
        RecordBatchPreview preview = JsonSerializer.Deserialize<RecordBatchPreview>(row.SummaryJson)
            ?? throw new InvalidOperationException("The stored preview summary could not be read.");
        return preview with { Applied = row.Applied };
    }

    private sealed class BatchPreviewRow
    {
        public required string RequestHash { get; init; }
        public required string MutationsJson { get; init; }
        public required string SummaryJson { get; init; }
        public required string ExpiresAtUtc { get; init; }
        public bool Applied { get; init; }
    }
}
