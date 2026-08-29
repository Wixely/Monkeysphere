using DnaX.Data.Migrations;

namespace Monkeysphere.Data;

public static class MonkeysphereSchema
{
    public static DnaXMigrationManifest Manifest { get; } = new(
        currentVersion: 4,
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
            DnaXMigration.Sql(2, "precision-aware-temporal-values", "Add temporal precision and approximation metadata", """
                ALTER TABLE FieldValues ADD COLUMN TemporalValue TEXT NULL;
                ALTER TABLE FieldValues ADD COLUMN TemporalPrecision INTEGER NULL
                    CHECK (TemporalPrecision IS NULL OR TemporalPrecision BETWEEN 0 AND 6);
                ALTER TABLE FieldValues ADD COLUMN TemporalSortKey TEXT NULL;
                ALTER TABLE FieldValues ADD COLUMN IsApproximate INTEGER NOT NULL DEFAULT 0
                    CHECK (IsApproximate IN (0, 1));
                ALTER TABLE FieldValues ADD COLUMN ApproximationNote TEXT NULL;

                CREATE INDEX IX_FieldValues_Field_Temporal
                    ON FieldValues(FieldDefinitionId, TemporalSortKey, TemporalPrecision);
                """),
            DnaXMigration.Sql(3, "record-relationships", "Add directional and symmetric record relationships", """
                CREATE TABLE RelationshipTypes (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    Directionality INTEGER NOT NULL CHECK (Directionality IN (0, 1)),
                    InverseName TEXT NULL COLLATE NOCASE,
                    Lifecycle INTEGER NOT NULL CHECK (Lifecycle IN (0, 1)),
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    CHECK ((Directionality = 0 AND InverseName IS NOT NULL) OR
                           (Directionality = 1 AND InverseName IS NULL))
                );

                CREATE TABLE Relationships (
                    Id TEXT NOT NULL PRIMARY KEY,
                    RelationshipTypeId TEXT NOT NULL,
                    SourceRecordId TEXT NOT NULL,
                    TargetRecordId TEXT NOT NULL,
                    Note TEXT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    CHECK (SourceRecordId <> TargetRecordId),
                    UNIQUE (RelationshipTypeId, SourceRecordId, TargetRecordId),
                    FOREIGN KEY (RelationshipTypeId) REFERENCES RelationshipTypes(Id) ON DELETE RESTRICT,
                    FOREIGN KEY (SourceRecordId) REFERENCES Records(Id) ON DELETE CASCADE,
                    FOREIGN KEY (TargetRecordId) REFERENCES Records(Id) ON DELETE CASCADE
                );

                CREATE INDEX IX_Relationships_Source
                    ON Relationships(SourceRecordId, RelationshipTypeId, TargetRecordId);
                CREATE INDEX IX_Relationships_Target
                    ON Relationships(TargetRecordId, RelationshipTypeId, SourceRecordId);
                """),
            DnaXMigration.Sql(4, "saved-grid-views", "Add reusable saved record views", """
                CREATE TABLE SavedViews (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    RecordTypeId TEXT NOT NULL,
                    Query TEXT NULL,
                    GroupByFieldDefinitionId TEXT NULL,
                    SortFieldDefinitionId TEXT NULL,
                    SortDescending INTEGER NOT NULL CHECK (SortDescending IN (0, 1)),
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    FOREIGN KEY (RecordTypeId) REFERENCES RecordTypes(Id) ON DELETE RESTRICT,
                    FOREIGN KEY (GroupByFieldDefinitionId) REFERENCES FieldDefinitions(Id) ON DELETE RESTRICT,
                    FOREIGN KEY (SortFieldDefinitionId) REFERENCES FieldDefinitions(Id) ON DELETE RESTRICT
                );

                CREATE TABLE SavedViewColumns (
                    SavedViewId TEXT NOT NULL,
                    FieldDefinitionId TEXT NOT NULL,
                    SortOrder INTEGER NOT NULL CHECK (SortOrder >= 0),
                    PRIMARY KEY (SavedViewId, FieldDefinitionId),
                    UNIQUE (SavedViewId, SortOrder),
                    FOREIGN KEY (SavedViewId) REFERENCES SavedViews(Id) ON DELETE CASCADE,
                    FOREIGN KEY (FieldDefinitionId) REFERENCES FieldDefinitions(Id) ON DELETE RESTRICT
                );

                CREATE TABLE SavedViewFilters (
                    SavedViewId TEXT NOT NULL,
                    SortOrder INTEGER NOT NULL CHECK (SortOrder >= 0),
                    FieldDefinitionId TEXT NOT NULL,
                    Operator INTEGER NOT NULL CHECK (Operator BETWEEN 0 AND 5),
                    Value TEXT NOT NULL,
                    PRIMARY KEY (SavedViewId, SortOrder),
                    FOREIGN KEY (SavedViewId) REFERENCES SavedViews(Id) ON DELETE CASCADE,
                    FOREIGN KEY (FieldDefinitionId) REFERENCES FieldDefinitions(Id) ON DELETE RESTRICT
                );

                CREATE INDEX IX_SavedViews_RecordType
                    ON SavedViews(RecordTypeId, Name, Id);
                CREATE INDEX IX_SavedViewColumns_Field
                    ON SavedViewColumns(FieldDefinitionId, SavedViewId);
                CREATE INDEX IX_SavedViewFilters_Field
                    ON SavedViewFilters(FieldDefinitionId, SavedViewId);
                """),
        ]);
}
