using DnaX.Data.Migrations.Sqlite.Testing;
using Monkeysphere.Data;

namespace Monkeysphere.Data.Tests;

public sealed class MigrationTests
{
    [Fact]
    public async Task EveryHistoricalSchemaUpgradesToTheCanonicalSchema()
    {
        DnaXHistoricalMigrationVerification result =
            await DnaXSqliteMigrationVerifier.VerifyAllHistoricalVersionsAsync(MonkeysphereSchema.Manifest);

        Assert.Equal(MonkeysphereSchema.Manifest.CurrentVersion, result.HistoricalVersions.Count);
        Assert.All(result.HistoricalVersions, version =>
            Assert.Equal(result.CanonicalSchemaSnapshot, version.SchemaSnapshot));
    }

    [Fact]
    public async Task DomainRegistrySchemaUpgradesToItsCanonicalSchema()
    {
        DnaXHistoricalMigrationVerification result =
            await DnaXSqliteMigrationVerifier.VerifyAllHistoricalVersionsAsync(DomainRegistrySchema.Manifest);

        Assert.Equal(DomainRegistrySchema.Manifest.CurrentVersion, result.HistoricalVersions.Count);
        Assert.All(result.HistoricalVersions, version =>
            Assert.Equal(result.CanonicalSchemaSnapshot, version.SchemaSnapshot));
    }
}
