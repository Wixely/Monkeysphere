using DnaX.Data.Migrations;
using DnaX.Data.Migrations.Sqlite.Testing;
using Monkeysphere.Data;

namespace Monkeysphere.Data.Tests;

public sealed class MigrationTests
{
    [Fact]
    public void ReleasedMigrationChecksumsRemainStable()
    {
        AssertChecksums(DomainRegistrySchema.Manifest,
            "sha256:92a2229c1bb7b216b072f59957cb6ce5457974aa74a239996350c628c84bde75",
            "sha256:efe2860cc91f2b57e44e8aed34c64f5f9592ae4e598983deaa91ab19a37a43e1",
            "sha256:8b01f8e638175ba8998be2ed9ae2f58b87aff8f5bb45dd85c5f4b439dc2ddcd9",
            "sha256:17831afa7d7c1f3666fe95d40617bbd92ad48d6f418ad5f55adca3193f8eabf4",
            "sha256:2eb1f66ddccc384dfb49e5a2338474ba701a313050c4cf4d552827125682c15c");

        AssertChecksums(MonkeysphereSchema.Manifest,
            "sha256:83526a45a8e61f30657a022434c9cf7eefc5276c8480d038b5a2e677a8d71ac1",
            "sha256:067476a31de8f52daf877569ed3b9f8200e77e4433445693c4a5d0e6055d1411",
            "sha256:cde8f13b365e475ac7021ecbe764bcde8ec1044eacadcdf26e4949a8047afd74",
            "sha256:ffb75e1429fefc1f048ab2e116fc5cb28f590c6f99e76103e9dbbcff428dc300",
            "sha256:c0dbc8f401d6e1515c4cfc03f24d68ad8b39d58d0f9129898dad124ab22de182",
            "sha256:fe21056066673d77d0fc9068370eeb6b1fbe80d0738782675e93d4d06b0847b6",
            "sha256:5a38d093e451f90fd0cfe77c94b9aaee0a4010cae16363400f1fca2d1d983ab4",
            "sha256:c15431d7bfb92d426463a929f5bd83d91233269e526bd3a7e755513be4b11dd4",
            "sha256:be2dcf7a37cd719a03e1ae2db91ab480822311d78747d235d0d63e9fe9cfd222",
            "sha256:13344a410108587d8598eae239dbda6cfae9ab267d1acf9bfe384d3b11e95237",
            "sha256:a2d612c38757d46c4e73d258e1cc860e59a928b4a06e024a44de29ee1cd8b1e6",
            "sha256:f6e5613609bb30760a1d67667af918af862e910d89d03e0acd8ae8f0b3f65aad",
            "sha256:b841928b5b0927e4a9e890003f82c0d4430673da7e2df4a3ba5607cae3e77042",
            "sha256:3a64ef9b72480064d1ca3a0edc1b5ff8c4e31d0abd34d339b3cec783bd0a5309",
            "sha256:fce29a82b7cfe0e8f84d495c054cc4d03ca4648d4bd1851a41fd3d4618e6f5fe",
            "sha256:e5a4fc376574b009327410657a21a4801da2b746c3f08a6b85380bcacc5bb0e6",
            "sha256:a4245f64f957c89cdcb2557e76ec650ed5a9448e23f19e94b55dd2ae7d81e076",
            "sha256:038353d16c5c78c4158bd1f47c052512203caad5b16065fe99b2306011d0907c",
            "sha256:60844178d85fdc2045df06096abe753064fe585ea71958060f31573b6f65de2f",
            "sha256:d1f852abc029efff1e3fcc3faea869a668e7feb3e5d0d21477b254593921374f",
            "sha256:330a24578ef885d39e4a8ad47cb6eb99d03de7588b886d279ac0f5c771f7c288",
            "sha256:a989b5e49348c946c39d8c8e54d6323839038764be3faeb22f42ea5cbdb5376a",
            "sha256:0781f776c80b9bdeb02a1101f2e536757dc8800d4b64ade1f69f4dda2444ad75",
            "sha256:7252755ff87b22704c391b9c6b2f0f2a536d6d84d9fa98b1eb902b83a87a88b7",
            "sha256:e1f0ba1c3d15a0fe88dc5b94d5f76b376aad3f52836f4ae81a4729a5f4455fe7",
            "sha256:61f3bfccd75d196ba1c577a0d3a875e82df089f6f413d8234899b5fc3d999eab",
            "sha256:b7d3dfeb4f4771d6be711c83b19b3f82848ca9f5946c0eeccc23830278d1d57f",
            "sha256:63ade6531ba26172a5f1ae387690053e489e5ea146344d45d83b018337752a71",
            "sha256:9eacc477b665e3c1437067f7655ae6e6dd5a7920c149392d6e0770e3ec26f3d4",
            "sha256:6af20497191d71f001ac59e6346241a5b09f5ea7820e25bb1a6fc89fa56a907a");

        AssertChecksums(RemoteUploadSchema.Manifest,
            "sha256:48112d7f6ce66e6dfa27e2042f0f72eae944e13fad96403c39676b631d5b8175",
            "sha256:01f29f238a61be8ae0a5f058851c1ee3708b866399076d6a3829d68cd750cd03",
            "sha256:5f37f946526f7d3609d8476234f70ab4dad429eff1b2e779bf5069d422c381f9",
            "sha256:0ae4a89de51f2c30bae4ed9a086c2a5983a5553c6c32f85ec53e82b3cc7d4471");
    }

    [Fact]
    public async Task UploadStagingSchemaUpgradesToItsCanonicalSchema()
    {
        DnaXHistoricalMigrationVerification result =
            await DnaXSqliteMigrationVerifier.VerifyAllHistoricalVersionsAsync(RemoteUploadSchema.Manifest);
        Assert.Equal(RemoteUploadSchema.Manifest.CurrentVersion, result.HistoricalVersions.Count);
        Assert.All(result.HistoricalVersions, version => Assert.Equal(result.CanonicalSchemaSnapshot, version.SchemaSnapshot));
    }

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

    private static void AssertChecksums(DnaXMigrationManifest manifest, params string[] expected) =>
        Assert.Equal(expected, manifest.Migrations.Select(migration => migration.Checksum));
}
