using DnaX.Data.Migrations;

namespace Monkeysphere.Data;

public static class MonkeysphereSchema
{
    public static DnaXMigrationManifest Manifest { get; } = new(
        currentVersion: 18,
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
            DnaXMigration.Sql(5, "preset-catalogue-and-setup", "Add preset provenance and first-run setup state", """
                ALTER TABLE RecordTypes ADD COLUMN PresetKey TEXT NULL;
                ALTER TABLE RecordTypes ADD COLUMN PresetVersion INTEGER NULL
                    CHECK (PresetVersion IS NULL OR PresetVersion > 0);

                ALTER TABLE FieldDefinitions ADD COLUMN CanonicalKey TEXT NULL;
                ALTER TABLE FieldDefinitions ADD COLUMN PresetKey TEXT NULL;
                ALTER TABLE FieldDefinitions ADD COLUMN PresetVersion INTEGER NULL
                    CHECK (PresetVersion IS NULL OR PresetVersion > 0);

                ALTER TABLE RelationshipTypes ADD COLUMN PresetKey TEXT NULL;
                ALTER TABLE RelationshipTypes ADD COLUMN PresetVersion INTEGER NULL
                    CHECK (PresetVersion IS NULL OR PresetVersion > 0);

                CREATE UNIQUE INDEX UX_RecordTypes_PresetKey
                    ON RecordTypes(PresetKey) WHERE PresetKey IS NOT NULL;
                CREATE INDEX IX_FieldDefinitions_CanonicalKey
                    ON FieldDefinitions(CanonicalKey) WHERE CanonicalKey IS NOT NULL;
                CREATE INDEX IX_FieldDefinitions_PresetKey
                    ON FieldDefinitions(PresetKey) WHERE PresetKey IS NOT NULL;
                CREATE UNIQUE INDEX UX_RelationshipTypes_PresetKey
                    ON RelationshipTypes(PresetKey) WHERE PresetKey IS NOT NULL;

                CREATE TABLE SetupState (
                    Singleton INTEGER NOT NULL PRIMARY KEY CHECK (Singleton = 1),
                    StarterPackKey TEXT NOT NULL,
                    CompletedAtUtc TEXT NOT NULL
                );

                INSERT INTO SetupState (Singleton, StarterPackKey, CompletedAtUtc)
                SELECT 1, 'existing', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                WHERE EXISTS (SELECT 1 FROM RecordTypes);
                """),
            DnaXMigration.Sql(6, "record-aliases", "Add searchable alternate names to records", """
                CREATE TABLE RecordAliases (
                    RecordId TEXT NOT NULL,
                    Ordinal INTEGER NOT NULL CHECK (Ordinal >= 0),
                    Value TEXT NOT NULL COLLATE NOCASE,
                    PRIMARY KEY (RecordId, Ordinal),
                    UNIQUE (RecordId, Value),
                    FOREIGN KEY (RecordId) REFERENCES Records(Id) ON DELETE CASCADE
                );

                CREATE INDEX IX_RecordAliases_Value
                    ON RecordAliases(Value, RecordId);
                """),
            DnaXMigration.Sql(7, "record-images", "Add ordered image metadata to records", """
                CREATE TABLE RecordImages (
                    Id TEXT NOT NULL PRIMARY KEY,
                    RecordId TEXT NOT NULL,
                    Ordinal INTEGER NOT NULL CHECK (Ordinal >= 0),
                    OriginalFileName TEXT NOT NULL,
                    OriginalContentType TEXT NOT NULL,
                    OriginalByteLength INTEGER NOT NULL CHECK (OriginalByteLength > 0),
                    Width INTEGER NOT NULL CHECK (Width > 0),
                    Height INTEGER NOT NULL CHECK (Height > 0),
                    CreatedAtUtc TEXT NOT NULL,
                    UNIQUE (RecordId, Ordinal),
                    FOREIGN KEY (RecordId) REFERENCES Records(Id) ON DELETE CASCADE
                );

                CREATE INDEX IX_RecordImages_Record
                    ON RecordImages(RecordId, Ordinal, Id);
                """),
            DnaXMigration.Sql(8, "structured-locations", "Add structured location field values", """
                CREATE TABLE FieldValueLocations (
                    FieldValueId TEXT NOT NULL PRIMARY KEY,
                    DisplayContext TEXT NULL COLLATE NOCASE,
                    Latitude REAL NULL CHECK (Latitude IS NULL OR Latitude BETWEEN -90 AND 90),
                    Longitude REAL NULL CHECK (Longitude IS NULL OR Longitude BETWEEN -180 AND 180),
                    AccuracyMetres REAL NULL CHECK (AccuracyMetres IS NULL OR AccuracyMetres > 0),
                    ApproximationRadiusKilometres REAL NULL
                        CHECK (ApproximationRadiusKilometres IS NULL OR ApproximationRadiusKilometres > 0),
                    FOREIGN KEY (FieldValueId) REFERENCES FieldValues(Id) ON DELETE CASCADE,
                    CHECK ((Latitude IS NULL AND Longitude IS NULL) OR
                           (Latitude IS NOT NULL AND Longitude IS NOT NULL)),
                    CHECK (DisplayContext IS NOT NULL OR Latitude IS NOT NULL),
                    CHECK (AccuracyMetres IS NULL OR Latitude IS NOT NULL)
                );

                CREATE INDEX IX_FieldValueLocations_Context
                    ON FieldValueLocations(DisplayContext, FieldValueId);
                CREATE INDEX IX_FieldValueLocations_Coordinates
                    ON FieldValueLocations(Latitude, Longitude, FieldValueId)
                    WHERE Latitude IS NOT NULL;
                """),
            DnaXMigration.Sql(9, "record-type-lifecycle", "Add record-type lifecycle state", """
                ALTER TABLE RecordTypes ADD COLUMN Lifecycle INTEGER NOT NULL DEFAULT 0
                    CHECK (Lifecycle IN (0, 1));

                CREATE INDEX IX_RecordTypes_Lifecycle_Name
                    ON RecordTypes(Lifecycle, Name, Id);
                """),
            DnaXMigration.Sql(10, "in-app-reminders", "Add private reminders for eligible date values", """
                CREATE TABLE Reminders (
                    Id TEXT NOT NULL PRIMARY KEY,
                    RecordId TEXT NOT NULL,
                    FieldDefinitionId TEXT NOT NULL,
                    ValueOrdinal INTEGER NOT NULL CHECK (ValueOrdinal >= 0),
                    LeadDays INTEGER NOT NULL CHECK (LeadDays BETWEEN 0 AND 3650),
                    CreatedAtUtc TEXT NOT NULL,
                    DismissedAtUtc TEXT NULL,
                    FOREIGN KEY (RecordId) REFERENCES Records(Id) ON DELETE CASCADE,
                    FOREIGN KEY (FieldDefinitionId) REFERENCES FieldDefinitions(Id)
                );

                CREATE UNIQUE INDEX UX_Reminders_ActiveValueLead
                    ON Reminders(RecordId, FieldDefinitionId, ValueOrdinal, LeadDays)
                    WHERE DismissedAtUtc IS NULL;
                CREATE INDEX IX_Reminders_Active
                    ON Reminders(DismissedAtUtc, RecordId, FieldDefinitionId, ValueOrdinal, LeadDays);
                """),
            DnaXMigration.Sql(11, "vcard-provenance", "Add idempotent vCard import and semantic property provenance", """
                CREATE TABLE VCardImports (
                    Fingerprint TEXT NOT NULL,
                    RecordId TEXT NOT NULL,
                    SourceVersion TEXT NOT NULL CHECK (SourceVersion IN ('3.0', '4.0')),
                    ImportedAtUtc TEXT NOT NULL,
                    PRIMARY KEY (Fingerprint, RecordId),
                    FOREIGN KEY (RecordId) REFERENCES Records(Id) ON DELETE CASCADE
                );

                CREATE INDEX IX_VCardImports_Record
                    ON VCardImports(RecordId, ImportedAtUtc, Fingerprint);

                CREATE TABLE VCardProperties (
                    RecordId TEXT NOT NULL,
                    Ordinal INTEGER NOT NULL CHECK (Ordinal >= 0),
                    GroupName TEXT NULL,
                    PropertyName TEXT NOT NULL,
                    ParametersJson TEXT NOT NULL,
                    RawValue TEXT NOT NULL,
                    MappingKind INTEGER NOT NULL CHECK (MappingKind BETWEEN 0 AND 3),
                    FieldDefinitionId TEXT NULL,
                    ValueOrdinal INTEGER NULL CHECK (ValueOrdinal IS NULL OR ValueOrdinal >= 0),
                    PRIMARY KEY (RecordId, Ordinal),
                    FOREIGN KEY (RecordId) REFERENCES Records(Id) ON DELETE CASCADE,
                    FOREIGN KEY (FieldDefinitionId) REFERENCES FieldDefinitions(Id),
                    CHECK ((MappingKind = 3 AND FieldDefinitionId IS NOT NULL AND ValueOrdinal IS NOT NULL) OR
                           (MappingKind <> 3 AND FieldDefinitionId IS NULL AND ValueOrdinal IS NULL))
                );

                CREATE INDEX IX_VCardProperties_Field
                    ON VCardProperties(FieldDefinitionId, RecordId, ValueOrdinal)
                    WHERE FieldDefinitionId IS NOT NULL;
                """),
            DnaXMigration.Sql(12, "richer-record-images", "Add captions, cover selection, ordering, and non-destructive corrections", """
                ALTER TABLE RecordImages ADD COLUMN Caption TEXT NULL;
                ALTER TABLE RecordImages ADD COLUMN IsCover INTEGER NOT NULL DEFAULT 0
                    CHECK (IsCover IN (0, 1));
                ALTER TABLE RecordImages ADD COLUMN RotationQuarterTurns INTEGER NOT NULL DEFAULT 0
                    CHECK (RotationQuarterTurns BETWEEN 0 AND 3);
                ALTER TABLE RecordImages ADD COLUMN CropX INTEGER NULL CHECK (CropX IS NULL OR CropX >= 0);
                ALTER TABLE RecordImages ADD COLUMN CropY INTEGER NULL CHECK (CropY IS NULL OR CropY >= 0);
                ALTER TABLE RecordImages ADD COLUMN CropWidth INTEGER NULL CHECK (CropWidth IS NULL OR CropWidth > 0);
                ALTER TABLE RecordImages ADD COLUMN CropHeight INTEGER NULL CHECK (CropHeight IS NULL OR CropHeight > 0);

                UPDATE RecordImages
                SET IsCover = 1
                WHERE Id IN (
                    SELECT cover.Id
                    FROM RecordImages cover
                    WHERE cover.Ordinal = (
                        SELECT MIN(candidate.Ordinal)
                        FROM RecordImages candidate
                        WHERE candidate.RecordId = cover.RecordId));

                CREATE UNIQUE INDEX UX_RecordImages_Cover
                    ON RecordImages(RecordId) WHERE IsCover = 1;
                """),
            DnaXMigration.Sql(13, "location-spatial-index", "Add an approximation-aware R-tree for location map queries", """
                CREATE TABLE FieldValueLocationSpatialKeys (
                    RowId INTEGER PRIMARY KEY AUTOINCREMENT,
                    FieldValueId TEXT NOT NULL UNIQUE,
                    FOREIGN KEY (FieldValueId) REFERENCES FieldValueLocations(FieldValueId) ON DELETE CASCADE
                );

                CREATE VIRTUAL TABLE FieldValueLocationSpatial USING rtree(
                    RowId,
                    MinLongitude,
                    MaxLongitude,
                    MinLatitude,
                    MaxLatitude
                );

                INSERT INTO FieldValueLocationSpatialKeys (FieldValueId)
                SELECT FieldValueId
                FROM FieldValueLocations
                WHERE Latitude IS NOT NULL
                ORDER BY FieldValueId;

                INSERT INTO FieldValueLocationSpatial (RowId, MinLongitude, MaxLongitude, MinLatitude, MaxLatitude)
                SELECT keys.RowId,
                       CASE
                           WHEN location.ApproximationRadiusKilometres IS NULL THEN location.Longitude
                           WHEN abs(location.Latitude) >= 89.9 THEN -180
                           WHEN location.Longitude - (location.ApproximationRadiusKilometres / (111.32 * cos(location.Latitude * pi() / 180))) < -180 THEN -180
                           ELSE location.Longitude - (location.ApproximationRadiusKilometres / (111.32 * cos(location.Latitude * pi() / 180)))
                       END,
                       CASE
                           WHEN location.ApproximationRadiusKilometres IS NULL THEN location.Longitude
                           WHEN abs(location.Latitude) >= 89.9 THEN 180
                           WHEN location.Longitude + (location.ApproximationRadiusKilometres / (111.32 * cos(location.Latitude * pi() / 180))) > 180 THEN 180
                           ELSE location.Longitude + (location.ApproximationRadiusKilometres / (111.32 * cos(location.Latitude * pi() / 180)))
                       END,
                       max(-90, location.Latitude - coalesce(location.ApproximationRadiusKilometres, 0) / 111.32),
                       min(90, location.Latitude + coalesce(location.ApproximationRadiusKilometres, 0) / 111.32)
                FROM FieldValueLocations location
                INNER JOIN FieldValueLocationSpatialKeys keys ON keys.FieldValueId = location.FieldValueId
                WHERE location.Latitude IS NOT NULL;

                CREATE TRIGGER FieldValueLocations_Spatial_Insert
                AFTER INSERT ON FieldValueLocations
                WHEN NEW.Latitude IS NOT NULL
                BEGIN
                    INSERT INTO FieldValueLocationSpatialKeys (FieldValueId) VALUES (NEW.FieldValueId);
                    INSERT INTO FieldValueLocationSpatial (RowId, MinLongitude, MaxLongitude, MinLatitude, MaxLatitude)
                    VALUES (
                        last_insert_rowid(),
                        CASE
                            WHEN NEW.ApproximationRadiusKilometres IS NULL THEN NEW.Longitude
                            WHEN abs(NEW.Latitude) >= 89.9 THEN -180
                            WHEN NEW.Longitude - (NEW.ApproximationRadiusKilometres / (111.32 * cos(NEW.Latitude * pi() / 180))) < -180 THEN -180
                            ELSE NEW.Longitude - (NEW.ApproximationRadiusKilometres / (111.32 * cos(NEW.Latitude * pi() / 180)))
                        END,
                        CASE
                            WHEN NEW.ApproximationRadiusKilometres IS NULL THEN NEW.Longitude
                            WHEN abs(NEW.Latitude) >= 89.9 THEN 180
                            WHEN NEW.Longitude + (NEW.ApproximationRadiusKilometres / (111.32 * cos(NEW.Latitude * pi() / 180))) > 180 THEN 180
                            ELSE NEW.Longitude + (NEW.ApproximationRadiusKilometres / (111.32 * cos(NEW.Latitude * pi() / 180)))
                        END,
                        max(-90, NEW.Latitude - coalesce(NEW.ApproximationRadiusKilometres, 0) / 111.32),
                        min(90, NEW.Latitude + coalesce(NEW.ApproximationRadiusKilometres, 0) / 111.32));
                END;

                CREATE TRIGGER FieldValueLocations_Spatial_Delete
                AFTER DELETE ON FieldValueLocations
                BEGIN
                    DELETE FROM FieldValueLocationSpatial
                    WHERE RowId = (SELECT RowId FROM FieldValueLocationSpatialKeys WHERE FieldValueId = OLD.FieldValueId);
                    DELETE FROM FieldValueLocationSpatialKeys WHERE FieldValueId = OLD.FieldValueId;
                END;

                CREATE TRIGGER FieldValueLocations_Spatial_Update
                AFTER UPDATE OF Latitude, Longitude, ApproximationRadiusKilometres ON FieldValueLocations
                BEGIN
                    DELETE FROM FieldValueLocationSpatial
                    WHERE RowId = (SELECT RowId FROM FieldValueLocationSpatialKeys WHERE FieldValueId = NEW.FieldValueId);
                    INSERT OR IGNORE INTO FieldValueLocationSpatialKeys (FieldValueId)
                    SELECT NEW.FieldValueId WHERE NEW.Latitude IS NOT NULL;
                    INSERT INTO FieldValueLocationSpatial (RowId, MinLongitude, MaxLongitude, MinLatitude, MaxLatitude)
                    SELECT keys.RowId,
                           CASE
                               WHEN NEW.ApproximationRadiusKilometres IS NULL THEN NEW.Longitude
                               WHEN abs(NEW.Latitude) >= 89.9 THEN -180
                               WHEN NEW.Longitude - (NEW.ApproximationRadiusKilometres / (111.32 * cos(NEW.Latitude * pi() / 180))) < -180 THEN -180
                               ELSE NEW.Longitude - (NEW.ApproximationRadiusKilometres / (111.32 * cos(NEW.Latitude * pi() / 180)))
                           END,
                           CASE
                               WHEN NEW.ApproximationRadiusKilometres IS NULL THEN NEW.Longitude
                               WHEN abs(NEW.Latitude) >= 89.9 THEN 180
                               WHEN NEW.Longitude + (NEW.ApproximationRadiusKilometres / (111.32 * cos(NEW.Latitude * pi() / 180))) > 180 THEN 180
                               ELSE NEW.Longitude + (NEW.ApproximationRadiusKilometres / (111.32 * cos(NEW.Latitude * pi() / 180)))
                           END,
                           max(-90, NEW.Latitude - coalesce(NEW.ApproximationRadiusKilometres, 0) / 111.32),
                           min(90, NEW.Latitude + coalesce(NEW.ApproximationRadiusKilometres, 0) / 111.32)
                    FROM FieldValueLocationSpatialKeys keys
                    WHERE keys.FieldValueId = NEW.FieldValueId AND NEW.Latitude IS NOT NULL;
                END;
                """),
            DnaXMigration.Sql(14, "record-type-symbols", "Add optional visual symbols to record types", """
                ALTER TABLE RecordTypes ADD COLUMN Symbol TEXT NULL
                    CHECK (Symbol IS NULL OR length(Symbol) BETWEEN 1 AND 32);

                UPDATE RecordTypes
                SET Symbol = CASE PresetKey
                    WHEN 'monkeysphere.person' THEN '👤'
                    WHEN 'monkeysphere.cat' THEN '🐈'
                    WHEN 'monkeysphere.dog' THEN '🐕'
                    WHEN 'monkeysphere.small-pet' THEN '🐹'
                    WHEN 'monkeysphere.vehicle' THEN '🚗'
                    WHEN 'monkeysphere.video-game' THEN '🎮'
                    WHEN 'monkeysphere.board-game' THEN '🎲'
                    WHEN 'monkeysphere.book' THEN '📚'
                    WHEN 'monkeysphere.film-series' THEN '🎬'
                    WHEN 'monkeysphere.plant' THEN '🌿'
                    WHEN 'monkeysphere.home' THEN '🏠'
                    WHEN 'monkeysphere.workplace' THEN '💼'
                    WHEN 'monkeysphere.favourite-place' THEN '📍'
                    WHEN 'monkeysphere.trip' THEN '✈️'
                    WHEN 'monkeysphere.event' THEN '📅'
                    ELSE Symbol
                END
                WHERE PresetKey IS NOT NULL;
                """),
            DnaXMigration.Sql(15, "configurable-dashboard", "Add dashboard display and recurring-date settings", """
                CREATE TABLE DashboardSettings (
                    Singleton INTEGER NOT NULL PRIMARY KEY CHECK (Singleton = 1),
                    RecordTypeId TEXT NULL,
                    UpcomingDays INTEGER NOT NULL CHECK (UpcomingDays BETWEEN 1 AND 366),
                    UpdatedAtUtc TEXT NOT NULL,
                    FOREIGN KEY (RecordTypeId) REFERENCES RecordTypes(Id) ON DELETE SET NULL
                );

                CREATE TABLE DashboardRecurringFields (
                    FieldDefinitionId TEXT NOT NULL PRIMARY KEY,
                    SortOrder INTEGER NOT NULL UNIQUE CHECK (SortOrder >= 0),
                    FOREIGN KEY (FieldDefinitionId) REFERENCES FieldDefinitions(Id) ON DELETE CASCADE
                );
                """),
            DnaXMigration.Sql(16, "saved-graph-views", "Add reusable relationship graph filters", """
                CREATE TABLE GraphViews (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    DisplayMode INTEGER NOT NULL CHECK (DisplayMode BETWEEN 0 AND 2),
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE GraphViewRecords (
                    GraphViewId TEXT NOT NULL,
                    RecordId TEXT NOT NULL,
                    SortOrder INTEGER NOT NULL CHECK (SortOrder >= 0),
                    PRIMARY KEY (GraphViewId, RecordId),
                    UNIQUE (GraphViewId, SortOrder),
                    FOREIGN KEY (GraphViewId) REFERENCES GraphViews(Id) ON DELETE CASCADE,
                    FOREIGN KEY (RecordId) REFERENCES Records(Id) ON DELETE CASCADE
                );

                CREATE TABLE GraphViewRecordTypes (
                    GraphViewId TEXT NOT NULL,
                    RecordTypeId TEXT NOT NULL,
                    SortOrder INTEGER NOT NULL CHECK (SortOrder >= 0),
                    PRIMARY KEY (GraphViewId, RecordTypeId),
                    UNIQUE (GraphViewId, SortOrder),
                    FOREIGN KEY (GraphViewId) REFERENCES GraphViews(Id) ON DELETE CASCADE,
                    FOREIGN KEY (RecordTypeId) REFERENCES RecordTypes(Id) ON DELETE CASCADE
                );

                CREATE INDEX IX_GraphViewRecords_RecordId ON GraphViewRecords (RecordId);
                CREATE INDEX IX_GraphViewRecordTypes_RecordTypeId ON GraphViewRecordTypes (RecordTypeId);
                """),
            DnaXMigration.Sql(17, "ordered-dashboard-categories", "Allow multiple ordered record categories on the dashboard", """
                CREATE TABLE DashboardCategories (
                    RecordTypeId TEXT NOT NULL PRIMARY KEY,
                    SortOrder INTEGER NOT NULL UNIQUE CHECK (SortOrder >= 0),
                    FOREIGN KEY (RecordTypeId) REFERENCES RecordTypes(Id) ON DELETE CASCADE
                );

                INSERT INTO DashboardCategories (RecordTypeId, SortOrder)
                SELECT RecordTypeId, 0
                FROM DashboardSettings
                WHERE Singleton = 1 AND RecordTypeId IS NOT NULL;
                """),
            DnaXMigration.Sql(18, "external-map-tiles-setting", "Add an opt-in external map tile setting", """
                CREATE TABLE MapSettings (
                    Singleton INTEGER NOT NULL PRIMARY KEY CHECK (Singleton = 1),
                    ExternalTilesEnabled INTEGER NOT NULL CHECK (ExternalTilesEnabled IN (0, 1)),
                    UpdatedAtUtc TEXT NOT NULL
                );

                INSERT INTO MapSettings (Singleton, ExternalTilesEnabled, UpdatedAtUtc)
                VALUES (1, 0, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """),
        ]);
}
