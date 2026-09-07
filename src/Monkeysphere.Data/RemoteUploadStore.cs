using System.Globalization;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

internal sealed class RemoteUploadStore(RemoteUploadConnections connections, IDomainCatalog domains, TimeProvider timeProvider) : IRemoteUploadStore
{
    public async Task<UploadStatus> BeginAsync(UploadOwner owner, Guid idempotencyKey, UploadRequest request, CancellationToken cancellationToken = default)
    {
        ValidateOwner(owner);
        ArgumentNullException.ThrowIfNull(request);
        string digest = NormalizeDigest(request.Sha256);
        string contentType = request.ContentType?.Trim().ToLowerInvariant() ?? "";
        if (idempotencyKey == Guid.Empty || request.ByteLength is < 1 or > RemoteUploadLimits.MaximumContactBytes || contentType is not ("text/vcard" or "text/x-vcard"))
            throw new DomainValidationException("Supply a retry UUID, 1-5242880 bytes and text/vcard or text/x-vcard.");
        await using SqliteConnection connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        DateTimeOffset now = timeProvider.GetUtcNow();
        await CleanupCoreAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);
        SessionRow? existing = await connection.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition("""
            SELECT * FROM UploadSessions WHERE DomainId = @DomainId AND CredentialFingerprint = @Fingerprint AND IdempotencyKey = @Key;
            """, new { DomainId = Key(owner.DomainId), Fingerprint = owner.CredentialFingerprint.ToUpperInvariant(), Key = Key(idempotencyKey) }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.ByteLength != request.ByteLength || existing.Sha256 != digest || existing.ContentType != contentType)
                throw new UploadException("retry_conflict", "The retry key was used with different upload metadata.");
            if (Parse(existing.RetryUntilUtc) <= now) throw new UploadException("retry_expired", "The upload retry window has expired.");
            await WriteAuditAsync(connection, transaction, new(owner.DomainId, Guid.Parse(existing.Id), "uploads.begin", "replayed", owner.CorrelationId), now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Status(existing, now);
        }
        if (request.MaximumChunkBytes is < 1 or > RemoteUploadLimits.MaximumChunkBytes || request.ByteLength > (long)request.MaximumChunkBytes * RemoteUploadLimits.MaximumChunksPerUpload)
            throw new UploadException("limit_exceeded", "The declared upload cannot fit the effective transport and chunk-count limits.");
        QuotaRow quota = await connection.QuerySingleAsync<QuotaRow>(new CommandDefinition("""
            SELECT COUNT(*) AS Retained,
                COALESCE(SUM(CASE WHEN State IN ('receiving', 'sealed') THEN 1 ELSE 0 END), 0) AS Active,
                COALESCE(SUM(CASE WHEN State IN ('receiving', 'sealed') AND DomainId = @DomainId THEN 1 ELSE 0 END), 0) AS DomainActive,
                COALESCE(SUM(CASE WHEN State IN ('receiving', 'sealed') AND DomainId = @DomainId AND CredentialFingerprint = @Fingerprint THEN 1 ELSE 0 END), 0) AS OwnerActive,
                COALESCE(SUM(CASE WHEN State IN ('receiving', 'sealed') THEN ByteLength ELSE 0 END), 0) AS Reserved,
                COALESCE(SUM(CASE WHEN State IN ('receiving', 'sealed') THEN ByteLength - AcceptedBytes ELSE 0 END), 0) AS Remaining
            FROM UploadSessions;
            """, new { DomainId = Key(owner.DomainId), Fingerprint = owner.CredentialFingerprint.ToUpperInvariant() }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (quota.Retained >= RemoteUploadLimits.MaximumRetainedSessions || quota.Active >= RemoteUploadLimits.MaximumActiveSessions ||
            quota.DomainActive >= RemoteUploadLimits.MaximumActiveSessionsPerDomain || quota.OwnerActive >= RemoteUploadLimits.MaximumActiveSessionsPerOwner ||
            quota.Reserved + request.ByteLength > RemoteUploadLimits.MaximumReservedBytes)
            throw new UploadException("limit_exceeded", "The upload staging quota has been reached. Cancel unused uploads or wait for expiry.");
        connections.RequireFreeSpace(quota.Remaining + request.ByteLength);
        SessionRow row = new()
        {
            Id = Key(Guid.CreateVersion7()),
            DomainId = Key(owner.DomainId),
            CredentialFingerprint = owner.CredentialFingerprint.ToUpperInvariant(),
            IdempotencyKey = Key(idempotencyKey),
            ByteLength = request.ByteLength,
            Sha256 = digest,
            ContentType = contentType,
            State = "receiving",
            CreatedAtUtc = Timestamp(now),
            ExpiresAtUtc = Timestamp(now.AddMinutes(RemoteUploadLimits.LifetimeMinutes)),
            RetryUntilUtc = Timestamp(now.AddHours(RemoteUploadLimits.RetryWindowHours)),
            ForgetAfterUtc = Timestamp(now.AddHours(RemoteUploadLimits.RetryWindowHours).AddDays(RemoteUploadLimits.TombstoneDays))
        };
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO UploadSessions (Id, DomainId, CredentialFingerprint, IdempotencyKey, ByteLength, Sha256, ContentType,
                AcceptedBytes, State, CreatedAtUtc, ExpiresAtUtc, RetryUntilUtc, ForgetAfterUtc)
            VALUES (@Id, @DomainId, @CredentialFingerprint, @IdempotencyKey, @ByteLength, @Sha256, @ContentType,
                0, @State, @CreatedAtUtc, @ExpiresAtUtc, @RetryUntilUtc, @ForgetAfterUtc);
            """, row, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await WriteAuditAsync(connection, transaction, new(owner.DomainId, Guid.Parse(row.Id), "uploads.begin", "accepted", owner.CorrelationId), now, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return Status(row, now);
    }

    public Task<UploadStatus> WriteAsync(UploadOwner owner, Guid uploadId, long offset, byte[] content, string sha256, CancellationToken cancellationToken = default)
    {
        if (content is null || content.Length is < 1 or > RemoteUploadLimits.MaximumChunkBytes || offset < 0)
            throw new DomainValidationException("Supply a non-negative offset and 1-262144 decoded bytes.");
        content = (byte[])content.Clone();
        string digest = NormalizeDigest(sha256);
        if (Convert.ToHexString(SHA256.HashData(content)) != digest) throw new DomainValidationException("The chunk checksum does not match.");
        return MutateAsync(owner, uploadId, "uploads.write", async (connection, transaction, row) =>
        {
            RequireLive(row);
            if (offset < row.AcceptedBytes)
            {
                byte[]? accepted = await connection.QuerySingleOrDefaultAsync<byte[]>(new CommandDefinition(
                    "SELECT Content FROM UploadChunks WHERE UploadId = @Id AND Offset = @Offset;", new { row.Id, Offset = offset }, transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                if (accepted is null || !accepted.AsSpan().SequenceEqual(content)) throw new UploadException("retry_conflict", "The offset was accepted with different chunk bytes or boundaries.");
                return;
            }
            if (row.State != "receiving") throw new UploadException("upload_not_writable", "The upload is already sealed.");
            if (offset != row.AcceptedBytes || content.Length > row.ByteLength - offset)
                throw new DomainValidationException("Chunks must be sequential and must not exceed the declared length.");
            if (row.ChunkCount >= RemoteUploadLimits.MaximumChunksPerUpload)
                throw new UploadException("limit_exceeded", "The upload chunk count limit has been reached. Cancel and retry with larger chunks.");
            connections.RequireFreeSpace(content.Length);
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO UploadChunks (UploadId, Offset, Content, Sha256) VALUES (@Id, @Offset, @Content, @Digest);
                UPDATE UploadSessions SET AcceptedBytes = AcceptedBytes + @Length, ChunkCount = ChunkCount + 1 WHERE Id = @Id;
                """, new { row.Id, Offset = offset, Content = content, Digest = digest, Length = content.Length }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            row.AcceptedBytes += content.Length;
        }, cancellationToken);
    }

    public async Task<UploadStatus> GetAsync(UploadOwner owner, Guid uploadId, CancellationToken cancellationToken = default)
    {
        ValidateOwner(owner);
        await using SqliteConnection connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        SessionRow row = await RequireOwnedAsync(connection, null, owner, uploadId, cancellationToken).ConfigureAwait(false);
        return Status(row, timeProvider.GetUtcNow());
    }

    public Task<UploadStatus> SealAsync(UploadOwner owner, Guid uploadId, CancellationToken cancellationToken = default) =>
        MutateAsync(owner, uploadId, "uploads.seal", async (connection, transaction, row) =>
        {
            RequireLive(row);
            if (row.State == "sealed") return;
            if (row.AcceptedBytes != row.ByteLength) throw new DomainValidationException("The upload is incomplete.");
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await ReadChunksAsync(connection, transaction, row, (bytes, _) => { hash.AppendData(bytes.Span); return Task.CompletedTask; }, cancellationToken).ConfigureAwait(false);
            if (Convert.ToHexString(hash.GetHashAndReset()) != row.Sha256) throw new DomainValidationException("The completed upload checksum does not match.");
            await connection.ExecuteAsync(new CommandDefinition("UPDATE UploadSessions SET State = 'sealed' WHERE Id = @Id;", new { row.Id }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            row.State = "sealed";
        }, cancellationToken);

    public Task<UploadStatus> CancelAsync(UploadOwner owner, Guid uploadId, CancellationToken cancellationToken = default) =>
        MutateAsync(owner, uploadId, "uploads.cancel", async (connection, transaction, row) =>
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                DELETE FROM UploadChunks WHERE UploadId = @Id;
                UPDATE UploadSessions SET State = 'cancelled' WHERE Id = @Id;
                """, new { row.Id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            row.State = "cancelled";
        }, cancellationToken);

    public async Task CopySealedToAsync(UploadOwner owner, Guid uploadId, Stream destination, CancellationToken cancellationToken = default)
    {
        ValidateOwner(owner);
        ArgumentNullException.ThrowIfNull(destination);
        await using SqliteConnection connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        SessionRow row = await RequireOwnedAsync(connection, transaction, owner, uploadId, cancellationToken).ConfigureAwait(false);
        RequireLive(row);
        if (row.State != "sealed") throw new UploadException("upload_not_ready", "Seal the upload before consuming it.");
        await ReadChunksAsync(connection, transaction, row, (bytes, token) => destination.WriteAsync(bytes, token).AsTask(), cancellationToken).ConfigureAwait(false);
        RequireLive(row);
    }

    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        await CleanupCoreAsync(connection, transaction, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> ReadSealedAsync<T>(UploadOwner owner, Guid uploadId, Func<Stream, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken = default)
    {
        ValidateOwner(owner);
        ArgumentNullException.ThrowIfNull(read);
        await using SqliteConnection connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        T result;
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: true))
        {
            SessionRow row = await RequireOwnedAsync(connection, transaction, owner, uploadId, cancellationToken).ConfigureAwait(false);
            RequireLive(row);
            if (row.State != "sealed") throw new UploadException("upload_not_ready", "Seal the upload before consuming it.");
            using Stream stream = new UploadReadStream(connection, transaction, row, cancellationToken);
            result = await read(stream, cancellationToken).ConfigureAwait(false);
            if (stream.Position != row.ByteLength) throw new InvalidOperationException("The upload consumer did not read the complete content.");
        }
        RequireLive(await RequireOwnedAsync(connection, null, owner, uploadId, cancellationToken).ConfigureAwait(false));
        return result;
    }

    private sealed class UploadReadStream(SqliteConnection connection, SqliteTransaction transaction, SessionRow row,
        CancellationToken lifetimeToken) : Stream
    {
        private byte[] _chunk = [];
        private int _index;
        private long _position;
        private bool _disposed;
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => row.ByteLength;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count), lifetimeToken).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lifetimeToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length == 0 || _position == row.ByteLength) return 0;
            if (_index == _chunk.Length)
            {
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, cancellationToken);
                _chunk = await connection.QuerySingleOrDefaultAsync<byte[]>(new CommandDefinition(
                    "SELECT Content FROM UploadChunks WHERE UploadId = @Id AND Offset = @Offset;", new { row.Id, Offset = _position }, transaction,
                    cancellationToken: linked.Token)).ConfigureAwait(false) ?? throw new IOException("Staged upload content is incomplete.");
                if (_chunk.Length is 0 or > RemoteUploadLimits.MaximumChunkBytes || _chunk.Length > row.ByteLength - _position)
                    throw new IOException("Staged upload content is invalid.");
                _index = 0;
            }
            int count = Math.Min(buffer.Length, _chunk.Length - _index);
            _chunk.AsMemory(_index, count).CopyTo(buffer);
            _index += count;
            _position += count;
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            _chunk = [];
            base.Dispose(disposing);
        }
    }

    private async Task<UploadStatus> MutateAsync(UploadOwner owner, Guid id, string action, Func<SqliteConnection, SqliteTransaction, SessionRow, Task> mutation, CancellationToken cancellationToken)
    {
        ValidateOwner(owner);
        await using SqliteConnection connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        SessionRow row = await RequireOwnedAsync(connection, transaction, owner, id, cancellationToken).ConfigureAwait(false);
        await mutation(connection, transaction, row).ConfigureAwait(false);
        if (action == "uploads.cancel")
            await ContactImportPreviewStore.CleanupAsync(connection, transaction, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await WriteAuditAsync(connection, transaction, new(owner.DomainId, id, action, "accepted", owner.CorrelationId), timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return Status(row, timeProvider.GetUtcNow());
    }

    private static async Task ReadChunksAsync(SqliteConnection connection, SqliteTransaction transaction, SessionRow row,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> consume, CancellationToken cancellationToken)
    {
        long offset = 0;
        while (offset < row.ByteLength)
        {
            byte[]? bytes = await connection.QuerySingleOrDefaultAsync<byte[]>(new CommandDefinition(
                "SELECT Content FROM UploadChunks WHERE UploadId = @Id AND Offset = @Offset;", new { row.Id, Offset = offset }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0 || bytes.Length > RemoteUploadLimits.MaximumChunkBytes || bytes.Length > row.ByteLength - offset)
                throw new IOException("Staged upload content is incomplete.");
            await consume(bytes, cancellationToken).ConfigureAwait(false);
            offset += bytes.Length;
        }
    }

    private void ValidateOwner(UploadOwner owner)
    {
        owner.Validate();
        if (!domains.TryGet(owner.DomainId, out _)) throw new UploadException("not_found", "Upload domain was not found.");
    }

    private static async Task<SessionRow> RequireOwnedAsync(SqliteConnection connection, SqliteTransaction? transaction, UploadOwner owner, Guid id, CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition("""
            SELECT * FROM UploadSessions WHERE Id = @Id AND DomainId = @DomainId AND CredentialFingerprint = @Fingerprint;
            """, new { Id = Key(id), DomainId = Key(owner.DomainId), Fingerprint = owner.CredentialFingerprint.ToUpperInvariant() }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false) ?? throw new UploadException("not_found", "Upload was not found.");

    private void RequireLive(SessionRow row)
    {
        if (Parse(row.ExpiresAtUtc) <= timeProvider.GetUtcNow() || row.State == "expired") throw new UploadException("upload_expired", "The upload has expired.");
        if (row.State == "cancelled") throw new UploadException("upload_cancelled", "The upload was cancelled.");
    }

    private static async Task CleanupCoreAsync(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE UploadSessions SET State = 'expired' WHERE ExpiresAtUtc <= @Now AND State IN ('receiving', 'sealed');
            DELETE FROM UploadChunks WHERE UploadId IN (SELECT Id FROM UploadSessions WHERE State IN ('expired', 'cancelled'));
            DELETE FROM UploadSessions WHERE ForgetAfterUtc <= @Now;
            DELETE FROM UploadAudit WHERE OccurredAtUtc < @Retention;
            """, new { Now = Timestamp(now), Retention = Timestamp(now.AddDays(-7)) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await ContactImportPreviewStore.CleanupAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeDigest(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit)
        ? value.ToUpperInvariant() : throw new DomainValidationException("Supply a 64-character SHA-256 hexadecimal digest.");

    public async Task RecordAuditAsync(UploadAudit entry, CancellationToken cancellationToken = default)
    {
        if (entry.DomainId == Guid.Empty || entry.Action is not ("uploads.begin" or "uploads.write" or "uploads.seal" or "uploads.complete" or "uploads.cancel" or "uploads.status" or "contacts.preview" or "contacts.inspect" or "contacts.evidence" or "contacts.apply" or "contacts.result") ||
            entry.Outcome.Length is < 1 or > 64 || entry.Outcome.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')) || entry.CorrelationId.Length > 128)
            throw new DomainValidationException("The upload audit metadata is invalid.");
        await using SqliteConnection connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        await WriteAuditAsync(connection, transaction, entry, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static Task<int> WriteAuditAsync(SqliteConnection connection, SqliteTransaction transaction, UploadAudit entry, DateTimeOffset now, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO UploadAudit (DomainId, UploadId, Action, Outcome, CorrelationId, OccurredAtUtc)
            VALUES (@DomainId, @UploadId, @Action, @Outcome, @CorrelationId, @Now);
            DELETE FROM UploadAudit WHERE OccurredAtUtc < @Retention;
            DELETE FROM UploadAudit WHERE Id NOT IN (SELECT Id FROM UploadAudit ORDER BY Id DESC LIMIT 50000);
            """, new
        {
            DomainId = Key(entry.DomainId),
            UploadId = Key(entry.UploadId),
            entry.Action,
            entry.Outcome,
            entry.CorrelationId,
            Now = Timestamp(now),
            Retention = Timestamp(now.AddDays(-7))
        }, transaction, cancellationToken: cancellationToken));
    private static string Key(Guid id) => id.ToString("D");
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static UploadStatus Status(SessionRow row, DateTimeOffset now) => new(Guid.Parse(row.Id), row.ByteLength, row.AcceptedBytes,
        row.State is "receiving" or "sealed" && Parse(row.ExpiresAtUtc) <= now ? "expired" : row.State, Parse(row.ExpiresAtUtc));

    private sealed class QuotaRow
    {
        public int Retained { get; init; }
        public int Active { get; init; }
        public int DomainActive { get; init; }
        public int OwnerActive { get; init; }
        public long Reserved { get; init; }
        public long Remaining { get; init; }
    }

    private sealed class SessionRow
    {
        public required string Id { get; init; }
        public required string DomainId { get; init; }
        public required string CredentialFingerprint { get; init; }
        public required string IdempotencyKey { get; init; }
        public long ByteLength { get; init; }
        public long AcceptedBytes { get; set; }
        public int ChunkCount { get; init; }
        public required string Sha256 { get; init; }
        public required string ContentType { get; init; }
        public required string State { get; set; }
        public required string CreatedAtUtc { get; init; }
        public required string ExpiresAtUtc { get; init; }
        public required string RetryUntilUtc { get; init; }
        public required string ForgetAfterUtc { get; init; }
    }
}
