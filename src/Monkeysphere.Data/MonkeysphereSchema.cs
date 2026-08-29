using DnaX.Data.Migrations;

namespace Monkeysphere.Data;

public static class MonkeysphereSchema
{
    public static DnaXMigrationManifest Manifest { get; } = new(
        currentVersion: 1,
        migrations:
        [
            DnaXMigration.Sql(1, "initial-configurable-records", "Create configurable record storage", """
                CREATE TABLE RecordTypes (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE FieldDefinitions (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL COLLATE NOCASE,
                    TypeId TEXT NOT NULL,
                    ConfigurationJson TEXT NOT NULL,
                    Lifecycle INTEGER NOT NULL CHECK (Lifecycle IN (0, 1)),
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE RecordTypeFields (
                    RecordTypeId TEXT NOT NULL,
                    FieldDefinitionId TEXT NOT NULL,
                    SortOrder INTEGER NOT NULL CHECK (SortOrder >= 0),
                    IsRequired INTEGER NOT NULL CHECK (IsRequired IN (0, 1)),
                    PRIMARY KEY (RecordTypeId, FieldDefinitionId),
                    UNIQUE (RecordTypeId, SortOrder),
                    FOREIGN KEY (RecordTypeId) REFERENCES RecordTypes(Id) ON DELETE CASCADE,
                    FOREIGN KEY (FieldDefinitionId) REFERENCES FieldDefinitions(Id) ON DELETE RESTRICT
                );

                CREATE TABLE Records (
                    Id TEXT NOT NULL PRIMARY KEY,
                    RecordTypeId TEXT NOT NULL,
                    DisplayName TEXT NOT NULL COLLATE NOCASE,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    FOREIGN KEY (RecordTypeId) REFERENCES RecordTypes(Id) ON DELETE RESTRICT
                );

                CREATE TABLE FieldValues (
                    Id TEXT NOT NULL PRIMARY KEY,
                    RecordId TEXT NOT NULL,
                    FieldDefinitionId TEXT NOT NULL,
                    Ordinal INTEGER NOT NULL CHECK (Ordinal >= 0),
                    TextValue TEXT NULL,
                    NumberValue TEXT NULL,
                    NumberSortValue REAL NULL,
                    DateValue TEXT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    UNIQUE (RecordId, FieldDefinitionId, Ordinal),
                    FOREIGN KEY (RecordId) REFERENCES Records(Id) ON DELETE CASCADE,
                    FOREIGN KEY (FieldDefinitionId) REFERENCES FieldDefinitions(Id) ON DELETE RESTRICT
                );

                CREATE TABLE FieldValueTags (
                    FieldValueId TEXT NOT NULL,
                    Ordinal INTEGER NOT NULL CHECK (Ordinal >= 0),
                    Value TEXT NOT NULL COLLATE NOCASE,
                    PRIMARY KEY (FieldValueId, Ordinal),
                    UNIQUE (FieldValueId, Value),
                    FOREIGN KEY (FieldValueId) REFERENCES FieldValues(Id) ON DELETE CASCADE
                );

                CREATE INDEX IX_RecordTypeFields_FieldDefinitionId
                    ON RecordTypeFields(FieldDefinitionId);
                CREATE INDEX IX_Records_RecordType_DisplayName
                    ON Records(RecordTypeId, DisplayName, Id);
                CREATE INDEX IX_Records_UpdatedAt
                    ON Records(UpdatedAtUtc DESC, DisplayName, Id);
                CREATE INDEX IX_FieldValues_RecordId
                    ON FieldValues(RecordId, FieldDefinitionId, Ordinal);
                CREATE INDEX IX_FieldValues_Field_Text
                    ON FieldValues(FieldDefinitionId, TextValue);
                CREATE INDEX IX_FieldValues_Field_Number
                    ON FieldValues(FieldDefinitionId, NumberSortValue);
                CREATE INDEX IX_FieldValues_Field_Date
                    ON FieldValues(FieldDefinitionId, DateValue);
                CREATE INDEX IX_FieldValueTags_Value
                    ON FieldValueTags(Value, FieldValueId);
                """),
        ]);
}
