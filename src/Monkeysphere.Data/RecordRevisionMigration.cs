namespace Monkeysphere.Data;

internal static class RecordRevisionMigration
{
    // This builds the immutable SQL for migration 21. Do not change after release.
    public static string Sql { get; } = BuildSql();

    private static string BuildSql()
    {
        System.Text.StringBuilder sql = new("""
            ALTER TABLE RecordTypes ADD COLUMN Revision TEXT NOT NULL DEFAULT '';
            ALTER TABLE Records ADD COLUMN Revision TEXT NOT NULL DEFAULT '';
            UPDATE RecordTypes SET Revision = lower(hex(randomblob(16)));
            UPDATE Records SET Revision = lower(hex(randomblob(16)));

            CREATE TRIGGER RecordTypes_Revision_Insert AFTER INSERT ON RecordTypes BEGIN
                UPDATE RecordTypes SET Revision = lower(hex(randomblob(16))) WHERE Id = NEW.Id;
            END;
            CREATE TRIGGER RecordTypes_Revision_Update AFTER UPDATE OF Name, Lifecycle, Symbol ON RecordTypes BEGIN
                UPDATE RecordTypes SET Revision = lower(hex(randomblob(16))) WHERE Id = NEW.Id;
                UPDATE Records SET Revision = lower(hex(randomblob(16))) WHERE RecordTypeId = NEW.Id;
            END;
            CREATE TRIGGER Records_Revision_Insert AFTER INSERT ON Records BEGIN
                UPDATE Records SET Revision = lower(hex(randomblob(16))) WHERE Id = NEW.Id;
            END;
            CREATE TRIGGER Records_Revision_Update AFTER UPDATE OF DisplayName, RecordTypeId ON Records BEGIN
                UPDATE Records SET Revision = lower(hex(randomblob(16))) WHERE Id = NEW.Id;
            END;

            """);
        foreach (string operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            string[] prefixes = operation switch { "INSERT" => ["NEW"], "DELETE" => ["OLD"], _ => ["OLD", "NEW"] };
            string typeIds = string.Join(", ", prefixes.Select(prefix => prefix + ".RecordTypeId"));
            sql.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"""
                CREATE TRIGGER RecordTypeFields_Revision_{operation} AFTER {operation} ON RecordTypeFields BEGIN
                    UPDATE RecordTypes SET Revision = lower(hex(randomblob(16))) WHERE Id IN ({typeIds});
                    UPDATE Records SET Revision = lower(hex(randomblob(16))) WHERE RecordTypeId IN ({typeIds});
                END;
                """);
            foreach (string table in new[] { "RecordAliases", "FieldValues" })
            {
                string recordIds = string.Join(", ", prefixes.Select(prefix => prefix + ".RecordId"));
                sql.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"""
                    CREATE TRIGGER {table}_Revision_{operation} AFTER {operation} ON {table} BEGIN
                        UPDATE Records SET Revision = lower(hex(randomblob(16))) WHERE Id IN ({recordIds});
                    END;
                    """);
            }
            foreach (string table in new[] { "FieldValueTags", "FieldValueLocations" })
            {
                string valueIds = string.Join(", ", prefixes.Select(prefix => prefix + ".FieldValueId"));
                sql.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"""
                    CREATE TRIGGER {table}_Revision_{operation} AFTER {operation} ON {table} BEGIN
                        UPDATE Records SET Revision = lower(hex(randomblob(16)))
                        WHERE Id IN (SELECT RecordId FROM FieldValues WHERE Id IN ({valueIds}));
                    END;
                    """);
            }
        }
        sql.AppendLine("""
            CREATE TRIGGER FieldDefinitions_Revision_Update
            AFTER UPDATE OF Name, TypeId, ConfigurationJson, Lifecycle ON FieldDefinitions BEGIN
                UPDATE RecordTypes SET Revision = lower(hex(randomblob(16)))
                WHERE Id IN (SELECT RecordTypeId FROM RecordTypeFields WHERE FieldDefinitionId = NEW.Id);
                UPDATE Records SET Revision = lower(hex(randomblob(16)))
                WHERE RecordTypeId IN (SELECT RecordTypeId FROM RecordTypeFields WHERE FieldDefinitionId = NEW.Id);
            END;
            """);
        return sql.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
