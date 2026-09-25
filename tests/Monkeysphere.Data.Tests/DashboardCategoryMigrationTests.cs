using Dapper;
using DnaX.Data.Migrations;
using DnaX.Data.Migrations.Sqlite.Testing;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// Migration 38 changes what an absent dashboard category means, from "removed" to "new". That is the
/// right reading going forward and the wrong one applied backwards: a record type an operator had
/// already taken off their dashboard is absent from the stored list too, so a careless upgrade would
/// put every one of them back. The migration therefore writes a dismissal for each active type the
/// operator had not chosen, which is the only part of this change existing deployments experience.
///
/// This drives that statement over a database that actually holds a curated dashboard. It proves the
/// migration applies over real data — the seed inserts rows carrying a foreign key to RecordTypes, and
/// a migration that throws on upgrade is the worst outcome available. What it cannot prove is the
/// resulting rows: the verifier owns the database and does not hand it back, so the preservation
/// itself is reasoned from the statement and covered from the far side by
/// <see cref="DashboardCategoryVisibilityTests"/>, which exercises every reading once a deployment is
/// at this version.
/// </summary>
public sealed class DashboardCategoryMigrationTests
{
    [Fact]
    public async Task TheUpgradeAppliesOverADashboardSomebodyHadAlreadyArranged()
    {
        int seeded = 0;
        DnaXHistoricalMigrationVerification result = await DnaXSqliteMigrationVerifier.VerifyAllHistoricalVersionsAsync(
            MonkeysphereSchema.Manifest,
            options => options.BeforeMigrateAsync = async (context, cancellationToken) =>
            {
                // Only where the dashboard tables exist and migration 38 has not run: seeding earlier
                // would be inserting into tables that are not there yet, and later into a state this
                // change has already resolved.
                if (context.Status.DatabaseVersion is < 17 or >= 38)
                {
                    return;
                }

                // Two active types and a curated dashboard naming one of them, which is the shape the
                // seed has to preserve. The retired third is there because the seed excludes it, and a
                // statement that tripped over it would fail here.
                await context.Connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO RecordTypes (Id, Name, CreatedAtUtc, UpdatedAtUtc, Lifecycle) VALUES
                        ('11111111-1111-4111-8111-111111111111', 'People', @Now, @Now, 0),
                        ('22222222-2222-4222-8222-222222222222', 'Places', @Now, @Now, 0),
                        ('33333333-3333-4333-8333-333333333333', 'Retired', @Now, @Now, 1);

                    INSERT INTO DashboardSettings (Singleton, RecordTypeId, UpcomingDays, UpdatedAtUtc)
                    VALUES (1, '11111111-1111-4111-8111-111111111111', 90, @Now);

                    INSERT INTO DashboardCategories (RecordTypeId, SortOrder)
                    VALUES ('11111111-1111-4111-8111-111111111111', 0);
                    """,
                    new { Now = "2026-01-01T00:00:00.0000000+00:00" },
                    context.Transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                Interlocked.Increment(ref seeded);
            });

        // The seed ran on the versions that have a dashboard to curate, so the assertion below is
        // about a populated database rather than an empty one.
        Assert.True(seeded > 0, "No historical version was seeded, so this proved nothing.");
        Assert.Equal(MonkeysphereSchema.Manifest.CurrentVersion, result.HistoricalVersions.Count);
        Assert.All(result.HistoricalVersions, version =>
            Assert.Equal(result.CanonicalSchemaSnapshot, version.SchemaSnapshot));
    }
}
