using Dapper;
using DnaX.Data.Migrations;
using DnaX.Hosting;
using Microsoft.Data.Sqlite;

namespace Monkeysphere.Data;

public static class RemoteUploadSchema
{
    public const string DatabaseName = "MonkeysphereTransfers";
    public const string FileName = "remote-transfers.db";
    public static DnaXMigrationManifest Manifest { get; } = new(currentVersion: 4, migrations:
    [
        DnaXMigration.Sql(1, "contact-upload-staging", "Persist bounded contact upload sessions and chunks", """
            CREATE TABLE UploadSessions (
                Id TEXT NOT NULL PRIMARY KEY,
                DomainId TEXT NOT NULL,
                CredentialFingerprint TEXT NOT NULL,
                IdempotencyKey TEXT NOT NULL,
                ByteLength INTEGER NOT NULL CHECK (ByteLength BETWEEN 1 AND 5242880),
                Sha256 TEXT NOT NULL,
                ContentType TEXT NOT NULL,
                AcceptedBytes INTEGER NOT NULL DEFAULT 0 CHECK (AcceptedBytes >= 0 AND AcceptedBytes <= ByteLength),
                ChunkCount INTEGER NOT NULL DEFAULT 0 CHECK (ChunkCount BETWEEN 0 AND 256),
                State TEXT NOT NULL CHECK (State IN ('receiving', 'sealed', 'cancelled', 'expired')),
                CreatedAtUtc TEXT NOT NULL,
                ExpiresAtUtc TEXT NOT NULL,
                RetryUntilUtc TEXT NOT NULL,
                ForgetAfterUtc TEXT NOT NULL,
                UNIQUE (DomainId, CredentialFingerprint, IdempotencyKey)
            );
            CREATE INDEX IX_UploadSessions_Expiry ON UploadSessions (ExpiresAtUtc);
            CREATE INDEX IX_UploadSessions_Retention ON UploadSessions (ForgetAfterUtc);
            CREATE TABLE UploadChunks (
                UploadId TEXT NOT NULL REFERENCES UploadSessions(Id) ON DELETE CASCADE,
                Offset INTEGER NOT NULL CHECK (Offset >= 0),
                Content BLOB NOT NULL CHECK (length(Content) BETWEEN 1 AND 262144),
                Sha256 TEXT NOT NULL,
                PRIMARY KEY (UploadId, Offset)
            );
            """),
        DnaXMigration.Sql(2, "upload-audit", "Persist bounded redacted upload audit", """
            CREATE TABLE UploadAudit (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                DomainId TEXT NOT NULL,
                UploadId TEXT NOT NULL,
                Action TEXT NOT NULL,
                Outcome TEXT NOT NULL,
                CorrelationId TEXT NOT NULL,
                OccurredAtUtc TEXT NOT NULL
            );
            """),
        DnaXMigration.Sql(3, "contact-import-previews", "Persist bounded owned contact previews and paged payloads", """
            CREATE TABLE ContactImportPreviews (
                Id TEXT NOT NULL PRIMARY KEY,
                UploadId TEXT NOT NULL,
                DomainId TEXT NOT NULL,
                CredentialFingerprint TEXT NOT NULL,
                IdempotencyKey TEXT NOT NULL,
                RecordTypeId TEXT NOT NULL,
                RecordTypeName TEXT NOT NULL,
                Revision TEXT NOT NULL,
                ContactCount INTEGER NOT NULL CHECK (ContactCount BETWEEN 1 AND 1000),
                StoredBytes INTEGER NOT NULL CHECK (StoredBytes BETWEEN 0 AND 33554432),
                ExpiresAtUtc TEXT NOT NULL,
                ForgetAfterUtc TEXT NOT NULL,
                UNIQUE (DomainId, CredentialFingerprint, IdempotencyKey)
            );
            CREATE INDEX IX_ContactImportPreviews_Expiry ON ContactImportPreviews (ExpiresAtUtc);
            CREATE TABLE ContactImportPreviewContacts (
                PreviewId TEXT NOT NULL REFERENCES ContactImportPreviews(Id) ON DELETE CASCADE,
                Ordinal INTEGER NOT NULL CHECK (Ordinal BETWEEN 0 AND 999),
                Payload BLOB NOT NULL CHECK (length(Payload) BETWEEN 1 AND 33554432),
                PRIMARY KEY (PreviewId, Ordinal)
            );
            """),
        DnaXMigration.Sql(4, "upload-purposes", "Record the declared purpose of an upload and admit record images", """
            CREATE TABLE UploadSessionsWithPurpose (
                Id TEXT NOT NULL PRIMARY KEY,
                DomainId TEXT NOT NULL,
                CredentialFingerprint TEXT NOT NULL,
                IdempotencyKey TEXT NOT NULL,
                Purpose TEXT NOT NULL CHECK (Purpose IN ('contact_import', 'record_image')),
                ByteLength INTEGER NOT NULL CHECK (ByteLength BETWEEN 1 AND 10485760),
                Sha256 TEXT NOT NULL,
                ContentType TEXT NOT NULL,
                AcceptedBytes INTEGER NOT NULL DEFAULT 0 CHECK (AcceptedBytes >= 0 AND AcceptedBytes <= ByteLength),
                ChunkCount INTEGER NOT NULL DEFAULT 0 CHECK (ChunkCount BETWEEN 0 AND 256),
                State TEXT NOT NULL CHECK (State IN ('receiving', 'sealed', 'cancelled', 'expired')),
                CreatedAtUtc TEXT NOT NULL,
                ExpiresAtUtc TEXT NOT NULL,
                RetryUntilUtc TEXT NOT NULL,
                ForgetAfterUtc TEXT NOT NULL,
                UNIQUE (DomainId, CredentialFingerprint, IdempotencyKey)
            );
            INSERT INTO UploadSessionsWithPurpose (Id, DomainId, CredentialFingerprint, IdempotencyKey, Purpose, ByteLength,
                Sha256, ContentType, AcceptedBytes, ChunkCount, State, CreatedAtUtc, ExpiresAtUtc, RetryUntilUtc, ForgetAfterUtc)
            SELECT Id, DomainId, CredentialFingerprint, IdempotencyKey, 'contact_import', ByteLength,
                Sha256, ContentType, AcceptedBytes, ChunkCount, State, CreatedAtUtc, ExpiresAtUtc, RetryUntilUtc, ForgetAfterUtc
            FROM UploadSessions;
            CREATE TABLE UploadChunksWithPurpose (
                UploadId TEXT NOT NULL REFERENCES UploadSessionsWithPurpose(Id) ON DELETE CASCADE,
                Offset INTEGER NOT NULL CHECK (Offset >= 0),
                Content BLOB NOT NULL CHECK (length(Content) BETWEEN 1 AND 262144),
                Sha256 TEXT NOT NULL,
                PRIMARY KEY (UploadId, Offset)
            );
            INSERT INTO UploadChunksWithPurpose (UploadId, Offset, Content, Sha256)
            SELECT UploadId, Offset, Content, Sha256 FROM UploadChunks;
            DROP TABLE UploadChunks;
            DROP TABLE UploadSessions;
            ALTER TABLE UploadSessionsWithPurpose RENAME TO UploadSessions;
            ALTER TABLE UploadChunksWithPurpose RENAME TO UploadChunks;
            CREATE INDEX IX_UploadSessions_Expiry ON UploadSessions (ExpiresAtUtc);
            CREATE INDEX IX_UploadSessions_Retention ON UploadSessions (ForgetAfterUtc);
            CREATE INDEX IX_UploadSessions_Purpose ON UploadSessions (Purpose);
            """)
    ]);
}

internal sealed class RemoteUploadConnections(IDnaXPaths paths)
{
    internal SqliteConnection CreateConnection() => DomainRegistryConnectionFactory.Create(paths.ResolveWritable(RemoteUploadSchema.FileName));
    internal async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition("PRAGMA secure_delete = ON;", cancellationToken: cancellationToken)).ConfigureAwait(false);
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal void RequireFreeSpace(long bytes)
    {
        DriveInfo drive = new(Path.GetPathRoot(Path.GetFullPath(paths.ResolveWritable(RemoteUploadSchema.FileName)))!);
        if (drive.AvailableFreeSpace < bytes * 2 + 16L * 1024 * 1024)
            throw new Monkeysphere.Core.UploadException("limit_exceeded", "Insufficient staging space is available.");
    }
}
