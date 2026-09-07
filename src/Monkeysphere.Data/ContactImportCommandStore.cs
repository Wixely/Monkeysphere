using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

internal sealed class ContactImportCommandStore(
    MonkeysphereConnectionFactory connections,
    ICurrentDomain currentDomain,
    TimeProvider timeProvider) : IContactImportCommandStore
{
    public async Task<ContactImportReceipt?> FindAsync(
        ContactImportCommand command,
        CancellationToken cancellationToken = default)
    {
        Validate(command.Owner);
        command.Validate();
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        ReceiptRow? row = await connection.QuerySingleOrDefaultAsync<ReceiptRow>(new CommandDefinition("""
            SELECT * FROM ContactImportReceipts
            WHERE CredentialFingerprint = @Fingerprint AND IdempotencyKey = @Key;
            """, new
        {
            Fingerprint = command.Owner.CredentialFingerprint.ToUpperInvariant(),
            Key = Key(command.IdempotencyKey),
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        RequireSameRequest(row, command);
        return RequireReplayable(row, timeProvider.GetUtcNow());
    }

    public async Task<bool> WasPreviewAppliedAsync(
        UploadOwner owner,
        Guid previewId,
        CancellationToken cancellationToken = default)
    {
        Validate(owner);
        if (previewId == Guid.Empty)
        {
            throw new DomainValidationException("Supply a contact preview UUID.");
        }

        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleAsync<long>(new CommandDefinition("""
            SELECT COUNT(*) FROM ContactImportReceipts
            WHERE PreviewId = @PreviewId AND CredentialFingerprint = @Fingerprint;
            """, new
        {
            PreviewId = Key(previewId),
            Fingerprint = owner.CredentialFingerprint.ToUpperInvariant(),
        }, cancellationToken: cancellationToken)).ConfigureAwait(false) != 0;
    }

    public async Task<ContactImportReceipt> ExecuteAsync(
        ContactImportCommand command,
        IReadOnlyList<VCardPreparedImport> prepared,
        string expectedRevision,
        DateTimeOffset previewExpiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        Validate(command.Owner);
        command.Validate();
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.Count is < 1 or > VCardParser.MaximumCards)
        {
            throw new DomainValidationException("An import must contain 1-1000 prepared contacts.");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (previewExpiresAtUtc <= now)
        {
            throw new UploadException("preview_expired", "The contact preview expired before the import could begin.");
        }

        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        await CleanupAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);
        ReceiptRow? existing = await FindRowAsync(connection, transaction, command.Owner, command.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            RequireSameRequest(existing, command);
            return RequireReplayable(existing, now);
        }

        ReceiptRow? consumed = await connection.QuerySingleOrDefaultAsync<ReceiptRow>(new CommandDefinition(
            "SELECT * FROM ContactImportReceipts WHERE PreviewId = @PreviewId;",
            new { PreviewId = Key(command.PreviewId) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (consumed is not null)
        {
            throw new CommandReplayException("preview_consumed", "This contact preview was already applied with another retry key.");
        }

        QuotaRow quota = await connection.QuerySingleAsync<QuotaRow>(new CommandDefinition("""
            SELECT COUNT(*) AS Commands,
                COALESCE((SELECT COUNT(*) FROM ContactImportOutcomes), 0) AS Outcomes
            FROM ContactImportReceipts;
            """, transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (quota.Commands >= ContactImportCommandLimits.MaximumRetainedCommandsPerDomain ||
            quota.Outcomes + prepared.Count > ContactImportCommandLimits.MaximumRetainedOutcomesPerDomain)
        {
            throw new CommandReplayException("limit_exceeded", "The contact import receipt quota has been reached. Wait for older retry history to expire.");
        }

        List<ContactImportOutcome> outcomes = [];
        VCardImportResult result = await SqliteVCardStore.ApplyCoreAsync(
            connection, transaction, prepared, expectedRevision, now, cancellationToken, outcomes).ConfigureAwait(false);
        if (outcomes.Count != prepared.Count || outcomes.Select(outcome => outcome.ContactIndex).Distinct().Count() != prepared.Count)
        {
            throw new InvalidOperationException("Contact import outcomes are incomplete.");
        }

        DateTimeOffset retryUntil = now.AddHours(ContactImportCommandLimits.RetryWindowHours);
        string receiptId = Key(Guid.CreateVersion7());
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ContactImportReceipts
                (Id, CredentialFingerprint, IdempotencyKey, PreviewId, RequestHash,
                 Created, Merged, Replaced, Skipped, ContactCount, CompletedAtUtc, RetryUntilUtc, ForgetAfterUtc)
            VALUES
                (@Id, @CredentialFingerprint, @IdempotencyKey, @PreviewId, @RequestHash,
                 @Created, @Merged, @Replaced, @Skipped, @ContactCount, @CompletedAtUtc, @RetryUntilUtc, @ForgetAfterUtc);
            """, new
        {
            Id = receiptId,
            CredentialFingerprint = command.Owner.CredentialFingerprint.ToUpperInvariant(),
            IdempotencyKey = Key(command.IdempotencyKey),
            PreviewId = Key(command.PreviewId),
            RequestHash = command.RequestHash.ToUpperInvariant(),
            result.Created,
            result.Merged,
            result.Replaced,
            result.Skipped,
            ContactCount = prepared.Count,
            CompletedAtUtc = Stamp(now),
            RetryUntilUtc = Stamp(retryUntil),
            ForgetAfterUtc = Stamp(retryUntil.AddDays(ContactImportCommandLimits.TombstoneDays)),
        }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (ContactImportOutcome outcome in outcomes.OrderBy(item => item.ContactIndex))
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO ContactImportOutcomes (ReceiptId, ContactIndex, Action, RecordId, Revision)
                VALUES (@ReceiptId, @ContactIndex, @Action, @RecordId, @Revision);
                """, new
            {
                ReceiptId = receiptId,
                outcome.ContactIndex,
                Action = (int)outcome.Action,
                RecordId = outcome.RecordId?.ToString("D"),
                outcome.Revision,
            }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await WriteAuditAsync(connection, transaction, command.Owner, "committed", now, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return new(command.IdempotencyKey, command.PreviewId, result, prepared.Count, now, retryUntil);
    }

    public async Task<ContactImportReceipt> GetAsync(
        UploadOwner owner,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        Validate(owner);
        if (idempotencyKey == Guid.Empty)
        {
            throw new DomainValidationException("Supply an import retry UUID.");
        }

        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        ReceiptRow row = await connection.QuerySingleOrDefaultAsync<ReceiptRow>(new CommandDefinition("""
            SELECT * FROM ContactImportReceipts
            WHERE CredentialFingerprint = @Fingerprint AND IdempotencyKey = @Key;
            """, new
        {
            Fingerprint = owner.CredentialFingerprint.ToUpperInvariant(),
            Key = Key(idempotencyKey),
        }, cancellationToken: cancellationToken)).ConfigureAwait(false)
            ?? throw new RecordCommandNotFoundException("The contact import receipt was not found.");
        return RequireReplayable(row, timeProvider.GetUtcNow());
    }

    public async Task<IReadOnlyList<ContactImportOutcome>> ReadOutcomesAsync(
        UploadOwner owner,
        Guid idempotencyKey,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        Validate(owner);
        if (idempotencyKey == Guid.Empty || page is < 1 or > 10000 || pageSize is < 1 or > 100)
        {
            throw new DomainValidationException("Supply an import retry UUID, page 1-10000 and page size 1-100.");
        }

        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        ReceiptRow row = await FindRowAsync(connection, transaction, owner, idempotencyKey, cancellationToken).ConfigureAwait(false)
            ?? throw new RecordCommandNotFoundException("The contact import receipt was not found.");
        _ = RequireReplayable(row, timeProvider.GetUtcNow());
        OutcomeRow[] outcomes = (await connection.QueryAsync<OutcomeRow>(new CommandDefinition("""
            SELECT ContactIndex, Action, RecordId, Revision
            FROM ContactImportOutcomes WHERE ReceiptId = @ReceiptId
            ORDER BY ContactIndex LIMIT @PageSize OFFSET @Offset;
            """, new { ReceiptId = row.Id, PageSize = pageSize, Offset = (page - 1) * pageSize }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
        return outcomes.Select(outcome => new ContactImportOutcome(
            outcome.ContactIndex,
            (VCardImportAction)outcome.Action,
            outcome.RecordId is null ? null : Guid.Parse(outcome.RecordId),
            outcome.Revision)).ToArray();
    }

    private void Validate(UploadOwner owner)
    {
        owner.Validate();
        if (owner.DomainId != currentDomain.Id)
        {
            throw new DomainValidationException("The import owner does not match the selected domain.");
        }
    }

    private static Task<ReceiptRow?> FindRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UploadOwner owner,
        Guid idempotencyKey,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<ReceiptRow>(new CommandDefinition("""
            SELECT * FROM ContactImportReceipts
            WHERE CredentialFingerprint = @Fingerprint AND IdempotencyKey = @Key;
            """, new
        {
            Fingerprint = owner.CredentialFingerprint.ToUpperInvariant(),
            Key = Key(idempotencyKey),
        }, transaction, cancellationToken: cancellationToken));

    private static void RequireSameRequest(ReceiptRow row, ContactImportCommand command)
    {
        if (!string.Equals(row.PreviewId, Key(command.PreviewId), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(row.RequestHash, command.RequestHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandReplayException("retry_conflict", "The import retry key was used with another preview or selection payload.");
        }
    }

    private static ContactImportReceipt RequireReplayable(ReceiptRow row, DateTimeOffset now)
    {
        if (Parse(row.RetryUntilUtc) <= now)
        {
            throw new CommandReplayException("retry_expired", "The contact import retry window has expired.");
        }

        return new(
            Guid.Parse(row.IdempotencyKey),
            Guid.Parse(row.PreviewId),
            new(row.Created, row.Merged, row.Replaced, row.Skipped),
            row.ContactCount,
            Parse(row.CompletedAtUtc),
            Parse(row.RetryUntilUtc));
    }

    private static async Task CleanupAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM ContactImportOutcomes
            WHERE ReceiptId IN (SELECT Id FROM ContactImportReceipts WHERE RetryUntilUtc <= @Now);
            DELETE FROM ContactImportReceipts WHERE ForgetAfterUtc <= @Now;
            """, new { Now = Stamp(now) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static Task<int> WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UploadOwner owner,
        string outcome,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ApplicationCommandAudit (DomainId, Surface, Action, Outcome, CorrelationId, OccurredAtUtc)
            VALUES (@DomainId, 'mcp', 'contacts.import', @Outcome, @CorrelationId, @Now);
            DELETE FROM ApplicationCommandAudit WHERE OccurredAtUtc < @Retention;
            DELETE FROM ApplicationCommandAudit
            WHERE Id NOT IN (SELECT Id FROM ApplicationCommandAudit ORDER BY Id DESC LIMIT 50000);
            """, new
        {
            DomainId = Key(owner.DomainId),
            Outcome = outcome,
            owner.CorrelationId,
            Now = Stamp(now),
            Retention = Stamp(now.AddDays(-90)),
        }, transaction, cancellationToken: cancellationToken));

    private static string Key(Guid value) => value.ToString("D");
    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private sealed class QuotaRow
    {
        public long Commands { get; set; }
        public long Outcomes { get; set; }
    }

    private sealed class OutcomeRow
    {
        public int ContactIndex { get; set; }
        public int Action { get; set; }
        public string? RecordId { get; set; }
        public string? Revision { get; set; }
    }

    private sealed class ReceiptRow
    {
        public string Id { get; set; } = "";
        public string CredentialFingerprint { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string PreviewId { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public int Created { get; set; }
        public int Merged { get; set; }
        public int Replaced { get; set; }
        public int Skipped { get; set; }
        public int ContactCount { get; set; }
        public string CompletedAtUtc { get; set; } = "";
        public string RetryUntilUtc { get; set; } = "";
        public string ForgetAfterUtc { get; set; } = "";
    }
}
