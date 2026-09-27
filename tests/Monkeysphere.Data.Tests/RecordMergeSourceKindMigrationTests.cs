using Dapper;
using Microsoft.Extensions.DependencyInjection;
using DnaX.Data.Migrations;
using DnaX.Data.Migrations.Sqlite.Testing;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// Migration 40 widens the retained-source kind so a merged-away record can be archived there, and
/// SQLite cannot alter a CHECK, so the table is rebuilt.
///
/// The rebuild is the whole risk. Dropping the parent fires <c>ON DELETE SET NULL</c> on
/// <c>RecordSourceValues.ImportId</c>, which would unlink every retained line from the import it
/// arrived on without erroring — a silent loss of exactly the provenance the merge is supposed to
/// protect. Running the migration over an empty database, which is all the verifier does by itself,
/// would never touch that branch.
///
/// So this seeds a record with an attributed retained line first. It proves the migration applies
/// over real data rather than throwing, which for a table rebuild carrying six triggers and two
/// indexes is worth proving on its own.
/// </summary>
public sealed class RecordMergeSourceKindMigrationTests
{
    [Fact]
    public async Task TheRebuildAppliesOverRetainedMaterialThatIsAttributedToAnImport()
    {
        int seeded = 0;
        DnaXHistoricalMigrationVerification result = await DnaXSqliteMigrationVerifier.VerifyAllHistoricalVersionsAsync(
            MonkeysphereSchema.Manifest,
            options => options.BeforeMigrateAsync = async (context, cancellationToken) =>
            {
                // Retained source material arrives at 30 and the rebuild is 40, so anywhere in between
                // is a database this change has to carry across. Earlier the tables do not exist;
                // later the rebuild has already happened.
                if (context.Status.DatabaseVersion is < 30 or >= 40)
                {
                    return;
                }

                await context.Connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO RecordTypes (Id, Name, CreatedAtUtc, UpdatedAtUtc, Lifecycle)
                    VALUES ('11111111-1111-4111-8111-111111111111', 'Person', @Now, @Now, 0);

                    INSERT INTO Records (Id, RecordTypeId, DisplayName, CreatedAtUtc, UpdatedAtUtc)
                    VALUES ('22222222-2222-4222-8222-222222222222', '11111111-1111-4111-8111-111111111111',
                            'Ada Lovelace', @Now, @Now);

                    INSERT INTO RecordSourceImports (Id, RecordId, SourceKind, SourceFormat, Fingerprint, ImportedAtUtc)
                    VALUES ('33333333-3333-4333-8333-333333333333', '22222222-2222-4222-8222-222222222222',
                            'vcard', '4.0', 'seeded-fingerprint', @Now);

                    -- One line attributed to that import and one deliberately unattributed, because the
                    -- restore must put the first back and leave the second alone rather than inventing
                    -- an attribution for it.
                    INSERT INTO RecordSourceValues (RecordId, Ordinal, ImportId, Grouping, Name, ParametersJson, RawValue, Mapping, FieldDefinitionId, ValueOrdinal)
                    VALUES ('22222222-2222-4222-8222-222222222222', 0, '33333333-3333-4333-8333-333333333333',
                            NULL, 'X-CUSTOM', '{}', 'kept verbatim', 0, NULL, NULL),
                           ('22222222-2222-4222-8222-222222222222', 1, NULL,
                            NULL, 'X-ORPHAN', '{}', 'no import', 0, NULL, NULL);
                    """, new { Now = "2026-01-01T00:00:00.0000000+00:00" }, context.Transaction,
                    cancellationToken: cancellationToken));
                seeded++;
            });

        Assert.True(seeded > 0, "no historical version carried retained source material, so the rebuild was never exercised with data");
        Assert.Equal(MonkeysphereSchema.Manifest.CurrentVersion, result.HistoricalVersions.Count);

        // Every historical path has to land on the same schema. A rebuild that recreated the table
        // but forgot an index or a trigger would diverge here rather than passing quietly.
        Assert.All(result.HistoricalVersions, version =>
            Assert.Equal(result.CanonicalSchemaSnapshot, version.SchemaSnapshot));
    }

    [Fact]
    public async Task AMergeSourceKindIsAcceptedOnceTheRebuildHasRunAndNothingElseIs()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        await using var connection = await application.Services
            .GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();

        await connection.ExecuteAsync("""
            INSERT INTO RecordTypes (Id, Name, CreatedAtUtc, UpdatedAtUtc, Lifecycle)
            VALUES ('11111111-1111-4111-8111-111111111111', 'Person', @Now, @Now, 0);
            INSERT INTO Records (Id, RecordTypeId, DisplayName, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('22222222-2222-4222-8222-222222222222', '11111111-1111-4111-8111-111111111111', 'Ada', @Now, @Now);
            """, new { Now = "2026-01-01T00:00:00.0000000+00:00" });

        // The point of the migration: 'merge' is now a kind the table will hold.
        await connection.ExecuteAsync("""
            INSERT INTO RecordSourceImports (Id, RecordId, SourceKind, SourceFormat, Fingerprint, ImportedAtUtc)
            VALUES ('33333333-3333-4333-8333-333333333333', '22222222-2222-4222-8222-222222222222', 'merge', NULL, NULL, @Now);
            """, new { Now = "2026-01-01T00:00:00.0000000+00:00" });
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM RecordSourceImports WHERE SourceKind = 'merge';"));

        // And the constraint still constrains: widening it to two kinds must not have widened it to any.
        Microsoft.Data.Sqlite.SqliteException rejected = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => connection.ExecuteAsync("""
                INSERT INTO RecordSourceImports (Id, RecordId, SourceKind, SourceFormat, Fingerprint, ImportedAtUtc)
                VALUES ('44444444-4444-4444-8444-444444444444', '22222222-2222-4222-8222-222222222222', 'invented', NULL, NULL, @Now);
                """, new { Now = "2026-01-01T00:00:00.0000000+00:00" }));
        Assert.Contains("CHECK", rejected.Message, StringComparison.OrdinalIgnoreCase);

        // The triggers came back with the table: writing an import still moves the record's deletion
        // revision, which is what the deletion preview's staleness check depends on.
        string? revision = await connection.ExecuteScalarAsync<string>(
            "SELECT DeletionRevision FROM Records WHERE Id = '22222222-2222-4222-8222-222222222222';");
        Assert.False(string.IsNullOrWhiteSpace(revision));
    }
}
