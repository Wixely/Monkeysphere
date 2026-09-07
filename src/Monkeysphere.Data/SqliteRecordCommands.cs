using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed partial class SqliteMonkeysphereStore : IRecordCommandStore
{
    public async Task<RecordCommandReceipt?> GetReceiptAsync(RecordCommandIdentity identity, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity);
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadReceiptAsync(connection, null, identity, now, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RecordCommandReceipt> ExecuteAsync(RecordCommandIdentity identity, IReadOnlyList<PreparedRecordMutation> mutations,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity);
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        RecordCommandReceipt receipt = await ExecuteCommandCoreAsync(connection, transaction, identity, mutations, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    private static async Task<RecordCommandReceipt> ExecuteCommandCoreAsync(SqliteConnection connection, SqliteTransaction transaction,
        RecordCommandIdentity identity, IReadOnlyList<PreparedRecordMutation> mutations, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RecordCommandReceipt? replay = await ReadReceiptAsync(connection, transaction, identity, now, cancellationToken).ConfigureAwait(false);
        if (replay is not null) return replay;

        if (mutations.Count is < 1 or > RecordCommandLimits.MaximumBatchRecords || mutations.Select(mutation => mutation.Id).Distinct().Count() != mutations.Count ||
            mutations.Any(mutation => mutation.Id == Guid.Empty || !Enum.IsDefined(mutation.Kind) ||
                (mutation.Kind == RecordMutationKind.Replace && string.IsNullOrWhiteSpace(mutation.ExpectedRevision))))
        {
            throw new DomainValidationException("Commands require 1-100 distinct record mutations and revisions for updates.");
        }
        if (identity.Action is not ("records.create" or "records.patch" or "records.batch") ||
            (identity.Action == "records.create" && (mutations.Count != 1 || mutations[0].Kind != RecordMutationKind.Create)) ||
            (identity.Action == "records.patch" && (mutations.Count != 1 || mutations[0].Kind != RecordMutationKind.Replace)))
        {
            throw new DomainValidationException("The command action does not match its mutations.");
        }

        await RequireCommandCapacityAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);

        List<RecordMutationOutcome> outcomes = [];
        foreach (PreparedRecordMutation mutation in mutations)
        {
            RecordDetails result;
            PreparedRecord record = mutation.Record;
            if (mutation.Kind == RecordMutationKind.Create)
            {
                await RequireRevisionAsync(connection, transaction, record.RecordTypeId, record.SchemaRevision, schema: true, cancellationToken).ConfigureAwait(false);
                result = await InsertRecordCoreAsync(connection, transaction, mutation.Id, record.RecordTypeId,
                    record.DisplayName, record.Aliases, record.Values, now, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RequireRevisionAsync(connection, transaction, mutation.Id, mutation.ExpectedRevision!, schema: false, cancellationToken).ConfigureAwait(false);
                result = await ReplaceRecordCoreAsync(connection, transaction, mutation.Id, record.DisplayName,
                    record.Aliases, record.Values, now, cancellationToken).ConfigureAwait(false);
            }
            outcomes.Add(new(mutation.Id, result.Revision, mutation.Kind == RecordMutationKind.Create ? "created" : "updated"));
        }

        return await SaveCommandReceiptAsync(connection, transaction, identity, outcomes, now, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RequireCommandCapacityAsync(SqliteConnection connection, SqliteTransaction transaction,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM RecordCommandReceipts WHERE ForgetAfterUtc <= @Now;
            UPDATE RecordCommandReceipts SET ReceiptJson = NULL WHERE RetryUntilUtc <= @Now AND ReceiptJson IS NOT NULL;
            """, new { Now = Timestamp(now) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        int count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM RecordCommandReceipts;", transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (count >= RecordCommandLimits.MaximumRetainedCommandsPerDomain) throw new DomainValidationException("The domain command history limit has been reached. Retry after older commands expire.");
    }

    private static async Task<RecordCommandReceipt> SaveCommandReceiptAsync(SqliteConnection connection, SqliteTransaction transaction,
        RecordCommandIdentity identity, IReadOnlyList<RecordMutationOutcome> outcomes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RecordCommandReceipt receipt = new(identity.IdempotencyKey, outcomes, now.ToUniversalTime(), now.ToUniversalTime().AddHours(RecordCommandLimits.RetryWindowHours));
        string json = JsonSerializer.Serialize(receipt);
        if (Encoding.UTF8.GetByteCount(json) > RecordCommandLimits.MaximumReceiptBytes) throw new DomainValidationException("The command result exceeds the receipt limit.");
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO RecordCommandReceipts
                (Surface, CredentialFingerprint, Action, IdempotencyKey, RequestHash, ReceiptJson, CompletedAtUtc, RetryUntilUtc, ForgetAfterUtc)
            VALUES (@Surface, @CredentialFingerprint, @Action, @Key, @RequestHash, @ReceiptJson, @Now, @RetryUntil, @ForgetAfter);
            INSERT INTO ApplicationCommandAudit (DomainId, Surface, Action, Outcome, CorrelationId, OccurredAtUtc)
            VALUES (@DomainId, @Surface, @Action, 'committed', @Key, @Now);
            DELETE FROM ApplicationCommandAudit WHERE OccurredAtUtc < @AuditRetention;
            DELETE FROM ApplicationCommandAudit WHERE Id NOT IN (SELECT Id FROM ApplicationCommandAudit ORDER BY Id DESC LIMIT 50000);
            """, new
        {
            identity.Surface,
            CredentialFingerprint = identity.CredentialFingerprint.ToUpperInvariant(),
            identity.Action,
            Key = Key(identity.IdempotencyKey),
            RequestHash = identity.RequestHash.ToUpperInvariant(),
            ReceiptJson = json,
            Now = Timestamp(now),
            RetryUntil = Timestamp(receipt.RetryUntilUtc),
            ForgetAfter = Timestamp(receipt.RetryUntilUtc.AddDays(RecordCommandLimits.TombstoneRetentionDays)),
            DomainId = Key(identity.DomainId),
            AuditRetention = Timestamp(now.AddDays(-90)),
        }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return receipt;
    }

    private void ValidateIdentity(RecordCommandIdentity identity)
    {
        identity.Validate();
        if (identity.DomainId != currentDomain.Id) throw new DomainValidationException("The command belongs to a different domain.");
    }

    private static async Task<RecordCommandReceipt?> ReadReceiptAsync(SqliteConnection connection, SqliteTransaction? transaction,
        RecordCommandIdentity identity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        CommandReceiptRow? row = await connection.QuerySingleOrDefaultAsync<CommandReceiptRow>(new CommandDefinition("""
            SELECT RequestHash, ReceiptJson, RetryUntilUtc FROM RecordCommandReceipts
            WHERE Surface = @Surface AND CredentialFingerprint = @CredentialFingerprint AND Action = @Action AND IdempotencyKey = @Key;
            """, new { identity.Surface, CredentialFingerprint = identity.CredentialFingerprint.ToUpperInvariant(), identity.Action, Key = Key(identity.IdempotencyKey) },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null) return null;
        if (!string.Equals(row.RequestHash, identity.RequestHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandReplayException("retry_conflict", "The retry key was used with a different request.");
        }
        if (ParseTimestamp(row.RetryUntilUtc) <= now || row.ReceiptJson is null)
        {
            throw new CommandReplayException("retry_expired", "The retry window has expired. Inspect the existing result before issuing a new command.");
        }
        return JsonSerializer.Deserialize<RecordCommandReceipt>(row.ReceiptJson)
            ?? throw new InvalidOperationException("The command receipt could not be read.");
    }

    private sealed class CommandReceiptRow
    {
        public required string RequestHash { get; init; }
        public string? ReceiptJson { get; init; }
        public required string RetryUntilUtc { get; init; }
    }
    private async Task<RecordCommandReceipt> ExecuteEntityCommandAsync(RecordCommandIdentity identity, string action,
        DateTimeOffset now, Func<SqliteConnection, SqliteTransaction, Task<IReadOnlyList<RecordMutationOutcome>>> mutation, CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        if (identity.Action != action) throw new DomainValidationException("The command action does not match the mutation.");
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        RecordCommandReceipt? replay = await ReadReceiptAsync(connection, transaction, identity, now, cancellationToken).ConfigureAwait(false);
        if (replay is not null) return replay;
        await RequireCommandCapacityAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RecordMutationOutcome> outcomes = await mutation(connection, transaction).ConfigureAwait(false);
        RecordCommandReceipt receipt = await SaveCommandReceiptAsync(connection, transaction, identity, outcomes, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

}
