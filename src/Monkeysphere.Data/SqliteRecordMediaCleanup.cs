using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed partial class SqliteMonkeysphereStore
{
    private static async Task<bool> DeleteRecordCoreAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        bool exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM Records WHERE Id = @Id);",
            new { Id = Key(id) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (!exists) return false;
        int pending = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM RecordMediaCleanup;",
            transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (pending >= RecordCommandLimits.MaximumPendingMediaCleanupPerDomain)
            throw new RecordPreviewException("limit_exceeded", "Pending media cleanup has reached its domain limit. Complete cleanup before deleting more records.");
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM Records WHERE Id = @Id;
            INSERT INTO RecordMediaCleanup (RecordId) VALUES (@Id) ON CONFLICT(RecordId) DO NOTHING;
            """, new { Id = Key(id) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TryCleanupRecordMediaAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new DomainValidationException("A record ID is required for media cleanup.");
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int queued = await connection.ExecuteAsync(new CommandDefinition("UPDATE RecordMediaCleanup SET LastAttemptAtUtc = @Now WHERE RecordId = @Id;",
            new { Id = Key(id), Now = Timestamp(now) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (queued == 0) return true;
        // Acquire before the SQL transaction: an in-flight upload may still need to finish its database write.
        using IDisposable? mediaLease = await mediaLocks.TryAcquireAsync(currentDomain.Id, id, TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        if (mediaLease is null) return false;
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        bool pending = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM RecordMediaCleanup WHERE RecordId = @Id);",
            new { Id = Key(id) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (!pending) return true;
        bool recordExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM Records WHERE Id = @Id);",
            new { Id = Key(id) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        bool removed = false;
        if (!recordExists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                RecordImageStoragePaths.DeleteRecordDirectory(paths, currentDomain, id);
                removed = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The database deletion is committed; retain this item for a later cleanup attempt.
            }
        }
        string sql = removed ? "DELETE FROM RecordMediaCleanup WHERE RecordId = @Id;" :
            "UPDATE RecordMediaCleanup SET LastAttemptAtUtc = @Now WHERE RecordId = @Id;";
        await connection.ExecuteAsync(new CommandDefinition(sql, new { Id = Key(id), Now = Timestamp(now) },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    public async Task CleanupPendingRecordMediaAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        string[] ids = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT RecordId FROM RecordMediaCleanup ORDER BY LastAttemptAtUtc, RecordId LIMIT 100;",
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
        foreach (string id in ids)
            _ = await TryCleanupRecordMediaAsync(ParseGuid(id), now, cancellationToken).ConfigureAwait(false);
    }
}
