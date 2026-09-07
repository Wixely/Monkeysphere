using System.Globalization;
using System.Text;

namespace Monkeysphere.Data;

internal static class RecordDeletionMigration
{
    // Immutable migration 24 SQL; keep line endings consistent across platforms.
    public static string Sql { get; } = BuildSql();

    private static string BuildSql()
    {
        StringBuilder sql = new("""
            ALTER TABLE Records ADD COLUMN DeletionRevision TEXT NOT NULL DEFAULT '';
            UPDATE Records SET DeletionRevision = lower(hex(randomblob(16)));
            CREATE TRIGGER Records_DeletionRevision_Insert AFTER INSERT ON Records BEGIN
                UPDATE Records SET DeletionRevision = lower(hex(randomblob(16))) WHERE Id = NEW.Id;
            END;
            CREATE TRIGGER Records_DeletionRevision_Content AFTER UPDATE OF Revision ON Records BEGIN
                UPDATE Records SET DeletionRevision = lower(hex(randomblob(16))) WHERE Id = NEW.Id;
            END;
            CREATE TABLE RecordDeletionPreviews (
                Id TEXT NOT NULL PRIMARY KEY,
                Surface TEXT NOT NULL,
                CredentialFingerprint TEXT NOT NULL,
                IdempotencyKey TEXT NOT NULL,
                RequestHash TEXT NOT NULL,
                SummaryJson TEXT NOT NULL,
                ExpiresAtUtc TEXT NOT NULL,
                Applied INTEGER NOT NULL DEFAULT 0 CHECK (Applied IN (0, 1)),
                UNIQUE (Surface, CredentialFingerprint, IdempotencyKey)
            );
            CREATE INDEX IX_RecordDeletionPreviews_Expiry ON RecordDeletionPreviews (ExpiresAtUtc);
            CREATE TABLE RecordMediaCleanup (
                RecordId TEXT NOT NULL PRIMARY KEY,
                LastAttemptAtUtc TEXT NOT NULL DEFAULT ''
            );

            """);
        (string Table, string[] Columns)[] dependencies = [
            ("RecordImages", ["RecordId"]), ("Relationships", ["SourceRecordId", "TargetRecordId"]),
            ("Reminders", ["RecordId"]), ("GraphViewRecords", ["RecordId"]), ("GraphViewNodePositions", ["RecordId"]),
            ("VCardImports", ["RecordId"]), ("VCardProperties", ["RecordId"]),
        ];
        foreach (var dependency in dependencies)
        {
            foreach (string operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                string[] prefixes = operation switch { "INSERT" => ["NEW"], "DELETE" => ["OLD"], _ => ["OLD", "NEW"] };
                string ids = string.Join(", ", prefixes.SelectMany(prefix => dependency.Columns.Select(column => prefix + "." + column)));
                sql.AppendLine(CultureInfo.InvariantCulture, $"""
                    CREATE TRIGGER {dependency.Table}_DeletionRevision_{operation} AFTER {operation} ON {dependency.Table} BEGIN
                        UPDATE Records SET DeletionRevision = lower(hex(randomblob(16))) WHERE Id IN ({ids});
                    END;
                    """);
            }
        }
        return sql.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
