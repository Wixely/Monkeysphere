using System.Text;

namespace Monkeysphere.Data;

internal static class ContactImportRevisionMigration
{
    internal static string Sql { get; } = Build();

    private static string Build()
    {
        StringBuilder sql = new("""
            CREATE TABLE ContactImportState (
                Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                Revision TEXT NOT NULL
            );
            INSERT INTO ContactImportState (Id, Revision) VALUES (1, lower(hex(randomblob(16))));

            """);
        foreach (string table in new[] { "Records", "RecordTypes", "FieldDefinitions", "VCardImports", "VCardProperties" })
        {
            foreach (string operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                sql.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"""
                    CREATE TRIGGER {table}_ContactImport_{operation} AFTER {operation} ON {table} BEGIN
                        UPDATE ContactImportState SET Revision = lower(hex(randomblob(16))) WHERE Id = 1;
                    END;
                    """);
            }
        }
        return sql.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
