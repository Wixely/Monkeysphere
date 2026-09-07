using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

internal sealed partial class DomainCatalog : IDomainCommands
{
    public async Task<RecordCommandReceipt> RenameAsync(RecordCommandIdentity identity, string name, string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        identity.Validate();
        if (identity.Surface != "mcp" || identity.Action != "domains.rename") throw new DomainValidationException("The registry command identity is invalid.");
        string normalized = MonkeysphereDomains.NormalizeName(name);
        if (string.IsNullOrWhiteSpace(expectedRevision) || expectedRevision.Length > 128)
            throw new DomainValidationException("Supply the domain revision returned by discovery.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            using SqliteTransaction transaction = connection.BeginTransaction();
            DateTimeOffset now = timeProvider.GetUtcNow();
            RecordCommandReceipt? replay = await ReadRegistryReceiptAsync(connection, transaction, identity, now, cancellationToken).ConfigureAwait(false);
            if (replay is not null) return replay;
            bool exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM Domains WHERE Id = @Id);", new { Id = Key(identity.DomainId) }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (!exists) throw new RecordCommandNotFoundException("Domain was not found.");
            await RequireRegistryCapacityAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);
            await RenameCoreAsync(connection, transaction, identity.DomainId, normalized, expectedRevision, now, cancellationToken).ConfigureAwait(false);
            ImmutableArray<MonkeysphereDomain> snapshot = await ReadSnapshotAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            MonkeysphereDomain renamed = snapshot.Single(domain => domain.Id == identity.DomainId);
            RecordCommandReceipt receipt = new(identity.IdempotencyKey, [new(renamed.Id, renamed.Revision, "renamed")],
                now.ToUniversalTime(), now.ToUniversalTime().AddHours(RecordCommandLimits.RetryWindowHours));
            await SaveRegistryReceiptAsync(connection, transaction, identity, receipt, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            _domains = snapshot;
            return receipt;
        }
        finally { _gate.Release(); }
    }

    public async Task RecordFailureAsync(ApplicationCommandEvent entry, CancellationToken cancellationToken = default)
    {
        if (entry.DomainId == Guid.Empty || entry.Surface != "mcp" || entry.Action != "domains.rename" ||
            entry.Outcome.Length is < 1 or > 64 || entry.Outcome.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '.')) ||
            entry.CorrelationId.Length > 128) throw new DomainValidationException("The registry audit metadata is invalid.");
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        await WriteRegistryAuditAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RecordCommandReceipt?> ReadRegistryReceiptAsync(SqliteConnection connection, SqliteTransaction transaction,
        RecordCommandIdentity identity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RegistryReceiptRow? row = await connection.QuerySingleOrDefaultAsync<RegistryReceiptRow>(new CommandDefinition("""
            SELECT DomainId, RequestHash, ReceiptJson, RetryUntilUtc FROM DomainCommandReceipts
            WHERE Surface = @Surface AND CredentialFingerprint = @CredentialFingerprint AND Action = @Action AND IdempotencyKey = @IdempotencyKey;
            """, new
        {
            identity.Surface,
            CredentialFingerprint = identity.CredentialFingerprint.ToUpperInvariant(),
            identity.Action,
            IdempotencyKey = Key(identity.IdempotencyKey)
        }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null) return null;
        if (row.DomainId != Key(identity.DomainId) || !string.Equals(row.RequestHash, identity.RequestHash, StringComparison.OrdinalIgnoreCase))
            throw new CommandReplayException("retry_conflict", "The retry key was used with a different registry request.");
        if (row.ReceiptJson is null || ParseTimestamp(row.RetryUntilUtc) <= now)
            throw new CommandReplayException("retry_expired", "The retry window has expired. Inspect the domain before issuing a new command.");
        return JsonSerializer.Deserialize<RecordCommandReceipt>(row.ReceiptJson)
            ?? throw new InvalidOperationException("The domain receipt could not be read.");
    }

    private static async Task RequireRegistryCapacityAsync(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM DomainCommandReceipts WHERE ForgetAfterUtc <= @Now;
            UPDATE DomainCommandReceipts SET ReceiptJson = NULL WHERE RetryUntilUtc <= @Now AND ReceiptJson IS NOT NULL;
            """, new { Now = Timestamp(now) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        int count = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM DomainCommandReceipts;",
            transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (count >= DomainCommandLimits.MaximumRetainedCommands)
            throw new DomainValidationException("The registry command history limit has been reached. Retry after older commands expire.");
    }

    private static async Task SaveRegistryReceiptAsync(SqliteConnection connection, SqliteTransaction transaction,
        RecordCommandIdentity identity, RecordCommandReceipt receipt, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(receipt);
        if (Encoding.UTF8.GetByteCount(json) > RecordCommandLimits.MaximumReceiptBytes) throw new DomainValidationException("The registry receipt exceeds its size limit.");
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO DomainCommandReceipts (Surface, CredentialFingerprint, Action, IdempotencyKey, DomainId, RequestHash, ReceiptJson, RetryUntilUtc, ForgetAfterUtc)
            VALUES (@Surface, @CredentialFingerprint, @Action, @IdempotencyKey, @DomainId, @RequestHash, @ReceiptJson, @RetryUntilUtc, @ForgetAfterUtc);
            """, new
        {
            identity.Surface,
            CredentialFingerprint = identity.CredentialFingerprint.ToUpperInvariant(),
            identity.Action,
            IdempotencyKey = Key(identity.IdempotencyKey),
            DomainId = Key(identity.DomainId),
            RequestHash = identity.RequestHash.ToUpperInvariant(),
            ReceiptJson = json,
            RetryUntilUtc = Timestamp(receipt.RetryUntilUtc),
            ForgetAfterUtc = Timestamp(receipt.RetryUntilUtc.AddDays(RecordCommandLimits.TombstoneRetentionDays)),
        }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await WriteRegistryAuditAsync(connection, transaction, new(identity.DomainId, identity.Surface, identity.Action, "committed",
            Key(identity.IdempotencyKey), receipt.CompletedAtUtc), cancellationToken).ConfigureAwait(false);
    }

    private static Task<int> WriteRegistryAuditAsync(SqliteConnection connection, SqliteTransaction transaction,
        ApplicationCommandEvent entry, CancellationToken cancellationToken) => connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO DomainCommandAudit (DomainId, Surface, Action, Outcome, CorrelationId, OccurredAtUtc)
            VALUES (@DomainId, @Surface, @Action, @Outcome, @CorrelationId, @Now);
            DELETE FROM DomainCommandAudit WHERE OccurredAtUtc < @Retention;
            DELETE FROM DomainCommandAudit WHERE Id NOT IN (SELECT Id FROM DomainCommandAudit ORDER BY Id DESC LIMIT 50000);
            """, new
        {
            DomainId = Key(entry.DomainId),
            entry.Surface,
            entry.Action,
            entry.Outcome,
            entry.CorrelationId,
            Now = Timestamp(entry.OccurredAtUtc),
            Retention = Timestamp(entry.OccurredAtUtc.AddDays(-90))
        },
            transaction, cancellationToken: cancellationToken));

    private sealed class RegistryReceiptRow
    {
        public required string DomainId { get; init; }
        public required string RequestHash { get; init; }
        public string? ReceiptJson { get; init; }
        public required string RetryUntilUtc { get; init; }
    }
}
