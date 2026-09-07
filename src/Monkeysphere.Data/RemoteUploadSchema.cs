using Dapper;
using DnaX.Data.Migrations;
using DnaX.Hosting;
using Microsoft.Data.Sqlite;

namespace Monkeysphere.Data;

public static class RemoteUploadSchema
{
    public const string DatabaseName = "MonkeysphereTransfers";
    public const string FileName = "remote-transfers.db";
    public static DnaXMigrationManifest Manifest { get; } = new(currentVersion: 2, migrations:
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
