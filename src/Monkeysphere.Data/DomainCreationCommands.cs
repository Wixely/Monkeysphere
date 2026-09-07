using System.Collections.Immutable;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

internal sealed partial class DomainCatalog
{
    public Task<RecordCommandReceipt> CreateAsync(RecordCommandIdentity identity, string name, CancellationToken cancellationToken = default)
    {
        identity.Validate();
        if (identity.Surface != "mcp" || identity.Action != "domains.create" || identity.DomainId == MonkeysphereDomains.DefaultId)
            throw new DomainValidationException("Supply a new, non-Default domain ID for domain creation.");
        return CreateReservedAsync(identity, MonkeysphereDomains.NormalizeName(name), cancellationToken);
    }

    private async Task<RecordCommandReceipt> CreateReservedAsync(RecordCommandIdentity identity, string name, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            DomainCreationRow pending;
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                DateTimeOffset now = timeProvider.GetUtcNow();
                if (identity.Surface == "mcp")
                {
                    RecordCommandReceipt? replay = await ReadRegistryReceiptAsync(connection, transaction, identity, now, cancellationToken).ConfigureAwait(false);
                    if (replay is not null) return replay;
                }
                DomainCreationRow? existing = await connection.QuerySingleOrDefaultAsync<DomainCreationRow>(new CommandDefinition("""
                    SELECT * FROM DomainCreations WHERE Surface = @Surface AND
                    ((CredentialFingerprint = @CredentialFingerprint AND Action = @Action AND IdempotencyKey = @IdempotencyKey)
                    OR (@Surface = 'browser' AND Name = @Name));
                    """, new
                {
                    identity.Surface,
                    CredentialFingerprint = identity.CredentialFingerprint.ToUpperInvariant(),
                    identity.Action,
                    IdempotencyKey = Key(identity.IdempotencyKey),
                    Name = name
                }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                if (existing is not null)
                {
                    if (identity.Surface == "mcp" && (existing.DomainId != Key(identity.DomainId) || !string.Equals(existing.RequestHash, identity.RequestHash, StringComparison.OrdinalIgnoreCase)))
                        throw new CommandReplayException("retry_conflict", "The retry key was used with a different domain creation request.");
                    pending = existing;
                }
                else
                {
                    if (_domains.Any(domain => domain.Id == identity.DomainId || string.Equals(domain.Name, name, StringComparison.OrdinalIgnoreCase)))
                        throw new DomainValidationException("A domain with that ID or name already exists.");
                    // Never adopt storage not owned by a durable reservation.
                    if (Directory.Exists(paths.ResolveWritable(Path.Combine("domains", identity.DomainId.ToString("N")))))
                        throw new DomainValidationException("The proposed domain ID is unavailable. Supply a different ID.");
                    int pendingCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM DomainCreations;",
                        transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                    if (pendingCount >= DomainCommandLimits.MaximumPendingCreations)
                        throw new DomainValidationException("The pending domain creation limit has been reached. Retry existing commands first.");
                    if (identity.Surface == "mcp") await RequireRegistryCapacityAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);
                    pending = new()
                    {
                        DomainId = Key(identity.DomainId),
                        Name = name,
                        Surface = identity.Surface,
                        CredentialFingerprint = identity.CredentialFingerprint.ToUpperInvariant(),
                        Action = identity.Action,
                        IdempotencyKey = Key(identity.IdempotencyKey),
                        RequestHash = identity.RequestHash.ToUpperInvariant(),
                        CreatedAtUtc = Timestamp(now)
                    };
                    try
                    {
                        await connection.ExecuteAsync(new CommandDefinition("""
                            INSERT INTO DomainCreations (DomainId, Name, Surface, CredentialFingerprint, Action, IdempotencyKey, RequestHash, CreatedAtUtc)
                            VALUES (@DomainId, @Name, @Surface, @CredentialFingerprint, @Action, @IdempotencyKey, @RequestHash, @CreatedAtUtc);
                            """, pending, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                    }
                    catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
                    {
                        throw new DomainValidationException("The domain ID or name is already reserved.", exception);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            // After reservation, cancellation or failure leaves resumable intent, never an unowned database.
            return await CompleteCreationAsync(connection, pending, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task RecoverCreationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        IEnumerable<DomainCreationRow> pending = await connection.QueryAsync<DomainCreationRow>(new CommandDefinition(
            "SELECT * FROM DomainCreations ORDER BY CreatedAtUtc, DomainId;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (DomainCreationRow entry in pending)
            await CompleteCreationAsync(connection, entry, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RecordCommandReceipt> CompleteCreationAsync(SqliteConnection connection, DomainCreationRow pending, CancellationToken cancellationToken)
    {
        Guid id = Guid.ParseExact(pending.DomainId, "D");
        if (id == Guid.Empty || id == MonkeysphereDomains.DefaultId) throw new InvalidOperationException("Invalid domain creation reservation.");
        await domainDatabases.MigrateAsync(id, cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        DateTimeOffset now = timeProvider.GetUtcNow();
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO Domains (Id, Name, IsDefault, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@DomainId, @Name, 0, @CreatedAtUtc, @Now);
            """, new { pending.DomainId, pending.Name, pending.CreatedAtUtc, Now = Timestamp(now) }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        ImmutableArray<MonkeysphereDomain> snapshot = await ReadSnapshotAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        MonkeysphereDomain created = snapshot.Single(domain => domain.Id == id);
        RecordCommandReceipt receipt = new(Guid.ParseExact(pending.IdempotencyKey, "D"), [new(id, created.Revision, "created")],
            now.ToUniversalTime(), now.ToUniversalTime().AddHours(RecordCommandLimits.RetryWindowHours));
        if (pending.Surface == "mcp")
        {
            RecordCommandIdentity identity = new(id, pending.Surface, pending.CredentialFingerprint, pending.Action,
                receipt.IdempotencyKey, pending.RequestHash);
            await SaveRegistryReceiptAsync(connection, transaction, identity, receipt, cancellationToken).ConfigureAwait(false);
        }
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM DomainCreations WHERE DomainId = @DomainId;", new { pending.DomainId },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        _domains = snapshot;
        return receipt;
    }

    private sealed class DomainCreationRow
    {
        public required string DomainId { get; init; }
        public required string Name { get; init; }
        public required string Surface { get; init; }
        public required string CredentialFingerprint { get; init; }
        public required string Action { get; init; }
        public required string IdempotencyKey { get; init; }
        public required string RequestHash { get; init; }
        public required string CreatedAtUtc { get; init; }
    }
}
