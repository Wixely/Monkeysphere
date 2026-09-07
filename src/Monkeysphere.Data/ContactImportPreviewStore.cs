using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

internal sealed class ContactImportPreviewStore(RemoteUploadConnections connections, IDomainCatalog domains, TimeProvider timeProvider)
    : IContactImportPreviewStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.SnakeCaseLower) }
    };

    public async Task<T> WithPreviewAsync<T>(UploadOwner owner, Guid previewId, Func<VCardImportPreview, DateTimeOffset, CancellationToken, Task<T>> use,
        CancellationToken cancellationToken = default)
    {
        Validate(owner);
        await using SqliteConnection connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        // Reserve the staging writer until the application transaction finishes. Cancellation/cleanup cannot invalidate this lease mid-commit.
        using SqliteTransaction transaction = connection.BeginTransaction();
        PreviewRow row = await RequireOwnedAsync(connection, transaction, owner, previewId, cancellationToken).ConfigureAwait(false);
        IEnumerable<byte[]> payloads = await connection.QueryAsync<byte[]>(new CommandDefinition(
            "SELECT Payload FROM ContactImportPreviewContacts WHERE PreviewId = @Id ORDER BY Ordinal;", new { Id = row.Id }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        VCardContactPreview[] contacts = payloads.Select(payload => JsonSerializer.Deserialize<VCardContactPreview>(payload, JsonOptions)
            ?? throw new IOException("Stored contact preview is invalid.")).ToArray();
        if (contacts.Length != row.ContactCount) throw new IOException("Stored contact preview is incomplete.");
        return await use(new(Guid.Parse(row.RecordTypeId), row.RecordTypeName, contacts, row.Revision),
            DateTimeOffset.Parse(row.ExpiresAtUtc, CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContactImportPreviewHandle?> FindAsync(UploadOwner owner, Guid uploadId, Guid idempotencyKey, CancellationToken cancellationToken = default)
    {
        Validate(owner);
        if (uploadId == Guid.Empty || idempotencyKey == Guid.Empty) throw new DomainValidationException("Supply upload and retry UUIDs.");
        await using SqliteConnection connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        PreviewRow? row = await FindCoreAsync(connection, transaction, owner, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        if (row.UploadId != Key(uploadId)) throw new UploadException("retry_conflict", "The preview retry key was used with another upload.");
        await RequireLiveAsync(connection, transaction, row, cancellationToken).ConfigureAwait(false);
        return Handle(row);
    }

    public async Task<ContactImportPreviewHandle> SaveAsync(UploadOwner owner, Guid uploadId, Guid idempotencyKey, VCardImportPreview preview,
        CancellationToken cancellationToken = default)
    {
        ContactImportPreviewHandle? existing = await FindAsync(owner, uploadId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return existing;
        if (preview.RecordTypeId == Guid.Empty || string.IsNullOrWhiteSpace(preview.Revision) || preview.Contacts.Count is < 1 or > VCardParser.MaximumCards)
            throw new DomainValidationException("The contact preview is invalid.");
        List<byte[]> payloads = [];
        int storedBytes = 0;
        for (int index = 0; index < preview.Contacts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (preview.Contacts[index].Index != index) throw new DomainValidationException("Preview contact indexes must be consecutive.");
            using BoundedBuffer buffer = new(ContactImportPreviewLimits.MaximumBytes - storedBytes);
            JsonSerializer.Serialize(buffer, preview.Contacts[index], JsonOptions);
            byte[] payload = buffer.ToArray();
            storedBytes += payload.Length;
            payloads.Add(payload);
        }
        await using SqliteConnection connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        PreviewRow? replay = await FindCoreAsync(connection, transaction, owner, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.UploadId != Key(uploadId)) throw new UploadException("retry_conflict", "The preview retry key was used with another upload.");
            await RequireLiveAsync(connection, transaction, replay, cancellationToken).ConfigureAwait(false);
            return Handle(replay);
        }
        DateTimeOffset now = timeProvider.GetUtcNow();
        await CleanupAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);
        string? uploadExpiry = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            SELECT ExpiresAtUtc FROM UploadSessions WHERE Id = @UploadId AND DomainId = @DomainId
                AND CredentialFingerprint = @Fingerprint AND State = 'sealed' AND ExpiresAtUtc > @Now;
            """, new { UploadId = Key(uploadId), DomainId = Key(owner.DomainId), Fingerprint = owner.CredentialFingerprint.ToUpperInvariant(), Now = Stamp(now) }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (uploadExpiry is null) throw new UploadException("not_found", "A live sealed upload was not found.");
        QuotaRow quota = await connection.QuerySingleAsync<QuotaRow>(new CommandDefinition("""
            SELECT COUNT(*) AS Retained, COALESCE(SUM(StoredBytes), 0) AS Bytes,
                COALESCE(SUM(CASE WHEN StoredBytes > 0 THEN 1 ELSE 0 END), 0) AS Active,
                COALESCE(SUM(CASE WHEN StoredBytes > 0 AND DomainId = @DomainId THEN 1 ELSE 0 END), 0) AS DomainActive,
                COALESCE(SUM(CASE WHEN StoredBytes > 0 AND DomainId = @DomainId AND CredentialFingerprint = @Fingerprint THEN 1 ELSE 0 END), 0) AS OwnerActive
            FROM ContactImportPreviews;
            """, new { DomainId = Key(owner.DomainId), Fingerprint = owner.CredentialFingerprint.ToUpperInvariant() }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (quota.Retained >= ContactImportPreviewLimits.MaximumRetained || quota.Active >= ContactImportPreviewLimits.MaximumActive ||
            quota.DomainActive >= ContactImportPreviewLimits.MaximumActivePerDomain || quota.OwnerActive >= ContactImportPreviewLimits.MaximumActivePerOwner ||
            quota.Bytes + storedBytes > ContactImportPreviewLimits.MaximumStoredBytes)
            throw new UploadException("limit_exceeded", "The contact preview quota has been reached. Wait for expiry.");
        connections.RequireFreeSpace(storedBytes);
        DateTimeOffset expiry = DateTimeOffset.Parse(uploadExpiry, CultureInfo.InvariantCulture);
        if (expiry > now.AddMinutes(ContactImportPreviewLimits.LifetimeMinutes)) expiry = now.AddMinutes(ContactImportPreviewLimits.LifetimeMinutes);
        PreviewRow row = new()
        {
            Id = Key(Guid.CreateVersion7()),
            UploadId = Key(uploadId),
            DomainId = Key(owner.DomainId),
            CredentialFingerprint = owner.CredentialFingerprint.ToUpperInvariant(),
            IdempotencyKey = Key(idempotencyKey),
            RecordTypeId = Key(preview.RecordTypeId),
            RecordTypeName = preview.RecordTypeName,
            Revision = preview.Revision,
            ContactCount = preview.Contacts.Count,
            StoredBytes = storedBytes,
            ExpiresAtUtc = Stamp(expiry),
            ForgetAfterUtc = Stamp(now.AddHours(RemoteUploadLimits.RetryWindowHours).AddDays(RemoteUploadLimits.TombstoneDays))
        };
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ContactImportPreviews (Id, UploadId, DomainId, CredentialFingerprint, IdempotencyKey, RecordTypeId, RecordTypeName,
                Revision, ContactCount, StoredBytes, ExpiresAtUtc, ForgetAfterUtc)
            VALUES (@Id, @UploadId, @DomainId, @CredentialFingerprint, @IdempotencyKey, @RecordTypeId, @RecordTypeName,
                @Revision, @ContactCount, @StoredBytes, @ExpiresAtUtc, @ForgetAfterUtc);
            """, row, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        for (int index = 0; index < payloads.Count; index++)
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO ContactImportPreviewContacts (PreviewId, Ordinal, Payload) VALUES (@Id, @Ordinal, @Payload);",
                new { row.Id, Ordinal = index, Payload = payloads[index] }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await RemoteUploadStore.WriteAuditAsync(connection, transaction, new(owner.DomainId, uploadId, "contacts.preview", "accepted", owner.CorrelationId), now, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return Handle(row);
    }

    public async Task<ContactImportPreviewHandle> GetAsync(UploadOwner owner, Guid previewId, CancellationToken cancellationToken = default)
    {
        Validate(owner);
        await using SqliteConnection connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        return Handle(await RequireOwnedAsync(connection, transaction, owner, previewId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<VCardContactPreview>> ReadContactsAsync(UploadOwner owner, Guid previewId, int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        Validate(owner);
        if (page is < 1 or > 10000 || pageSize is < 1 or > ContactImportPreviewLimits.MaximumPageSize)
            throw new DomainValidationException("Supply page 1-10000 and page size 1-100.");
        await using SqliteConnection connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        _ = await RequireOwnedAsync(connection, transaction, owner, previewId, cancellationToken).ConfigureAwait(false);
        IEnumerable<byte[]> payloads = await connection.QueryAsync<byte[]>(new CommandDefinition("""
            SELECT Payload FROM ContactImportPreviewContacts WHERE PreviewId = @Id ORDER BY Ordinal LIMIT @Size OFFSET @Offset;
            """, new { Id = Key(previewId), Size = pageSize, Offset = (page - 1) * pageSize }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return payloads.Select(payload => JsonSerializer.Deserialize<VCardContactPreview>(payload, JsonOptions)
            ?? throw new IOException("Stored contact preview is invalid.")).ToArray();
    }

    public async Task<ContactImportEvidenceChunk> ReadEvidenceAsync(UploadOwner owner, Guid previewId, int contactIndex, int offset, int count,
        CancellationToken cancellationToken = default)
    {
        Validate(owner);
        if (contactIndex is < 0 or >= VCardParser.MaximumCards || offset is < 0 or > ContactImportPreviewLimits.MaximumBytes || count is < 1 or > 16384)
            throw new DomainValidationException("Supply contact index 0-999, a non-negative byte offset and count 1-16384.");
        await using SqliteConnection connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        PreviewRow preview = await RequireOwnedAsync(connection, transaction, owner, previewId, cancellationToken).ConfigureAwait(false);
        if (contactIndex >= preview.ContactCount) throw new UploadException("not_found", "The contact index was not found in this preview.");
        EvidenceRow evidence = await connection.QuerySingleAsync<EvidenceRow>(new CommandDefinition("""
            SELECT length(Payload) AS TotalBytes, substr(Payload, @Start, @Count) AS Content
            FROM ContactImportPreviewContacts WHERE PreviewId = @Id AND Ordinal = @Ordinal;
            """, new { Id = Key(previewId), Ordinal = contactIndex, Start = offset + 1, Count = count }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (offset > evidence.TotalBytes) throw new DomainValidationException("The offset exceeds the contact evidence length.");
        return new(contactIndex, offset, evidence.TotalBytes, evidence.Content);
    }

    private sealed class EvidenceRow
    {
        public int TotalBytes { get; set; }
        public byte[] Content { get; set; } = [];
    }

    private async Task<PreviewRow> RequireOwnedAsync(SqliteConnection connection, SqliteTransaction transaction, UploadOwner owner, Guid id, CancellationToken cancellationToken)
    {
        PreviewRow row = await connection.QuerySingleOrDefaultAsync<PreviewRow>(new CommandDefinition("""
            SELECT * FROM ContactImportPreviews WHERE Id = @Id AND DomainId = @DomainId AND CredentialFingerprint = @Fingerprint;
            """, new { Id = Key(id), DomainId = Key(owner.DomainId), Fingerprint = owner.CredentialFingerprint.ToUpperInvariant() }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false) ?? throw new UploadException("not_found", "Contact preview was not found.");
        await RequireLiveAsync(connection, transaction, row, cancellationToken).ConfigureAwait(false);
        return row;
    }

    private async Task RequireLiveAsync(SqliteConnection connection, SqliteTransaction transaction, PreviewRow row, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (DateTimeOffset.Parse(row.ExpiresAtUtc, CultureInfo.InvariantCulture) <= now || row.StoredBytes == 0)
            throw new UploadException("preview_expired", "The contact preview has expired. Create a new preview with a new retry key.");
        long live = await connection.QuerySingleAsync<long>(new CommandDefinition("""
            SELECT COUNT(*) FROM UploadSessions WHERE Id = @UploadId AND DomainId = @DomainId AND CredentialFingerprint = @CredentialFingerprint
                AND State = 'sealed' AND ExpiresAtUtc > @Now;
            """, new { row.UploadId, row.DomainId, row.CredentialFingerprint, Now = Stamp(now) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (live == 0) throw new UploadException("preview_unavailable", "The source upload is no longer available.");
    }

    private static Task<PreviewRow?> FindCoreAsync(SqliteConnection connection, SqliteTransaction transaction, UploadOwner owner, Guid key, CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<PreviewRow>(new CommandDefinition("""
            SELECT * FROM ContactImportPreviews WHERE DomainId = @DomainId AND CredentialFingerprint = @Fingerprint AND IdempotencyKey = @Key;
            """, new { DomainId = Key(owner.DomainId), Fingerprint = owner.CredentialFingerprint.ToUpperInvariant(), Key = Key(key) }, transaction, cancellationToken: cancellationToken));

    internal static Task<int> CleanupAsync(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition("""
            UPDATE ContactImportPreviews SET StoredBytes = 0, RecordTypeName = '', Revision = '' WHERE ExpiresAtUtc <= @Now OR UploadId NOT IN
                (SELECT Id FROM UploadSessions WHERE State = 'sealed' AND ExpiresAtUtc > @Now);
            DELETE FROM ContactImportPreviewContacts WHERE PreviewId IN (SELECT Id FROM ContactImportPreviews WHERE StoredBytes = 0);
            DELETE FROM ContactImportPreviews WHERE ForgetAfterUtc <= @Now;
            """, new { Now = Stamp(now) }, transaction, cancellationToken: cancellationToken));

    private void Validate(UploadOwner owner)
    {
        owner.Validate();
        if (!domains.TryGet(owner.DomainId, out _)) throw new UploadException("not_found", "Contact preview domain was not found.");
    }
    private static string Key(Guid id) => id.ToString("D");
    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static ContactImportPreviewHandle Handle(PreviewRow row) => new(Guid.Parse(row.Id), Guid.Parse(row.UploadId), Guid.Parse(row.RecordTypeId),
        row.RecordTypeName, row.Revision, row.ContactCount, DateTimeOffset.Parse(row.ExpiresAtUtc, CultureInfo.InvariantCulture));

    private sealed class BoundedBuffer(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Require(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Require(buffer.Length); base.Write(buffer); }
        private void Require(int count)
        {
            if (count > maximum - Length) throw new UploadException("limit_exceeded", "The complete preview exceeds the 32 MiB storage limit. Split the contact file.");
        }
    }
    private sealed class QuotaRow
    {
        public long Retained { get; set; }
        public long Bytes { get; set; }
        public long Active { get; set; }
        public long DomainActive { get; set; }
        public long OwnerActive { get; set; }
    }
    private sealed class PreviewRow
    {
        public string Id { get; set; } = "";
        public string UploadId { get; set; } = "";
        public string DomainId { get; set; } = "";
        public string CredentialFingerprint { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string RecordTypeId { get; set; } = "";
        public string RecordTypeName { get; set; } = "";
        public string Revision { get; set; } = "";
        public int ContactCount { get; set; }
        public int StoredBytes { get; set; }
        public string ExpiresAtUtc { get; set; } = "";
        public string ForgetAfterUtc { get; set; } = "";
    }
}
