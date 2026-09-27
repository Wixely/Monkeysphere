using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// Backups and operational status were browser-only, so a client could fill a deployment and never
/// check whether any of it was recoverable. These pin the three properties that make the remote view
/// safe to hand out: the backup grants are separable from each other and from reading records, a
/// package's metadata says nothing about what a record holds, and nothing here restores, prunes or
/// reschedules anything.
/// </summary>
public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task McpReportsOperationalStatusAndTakesAVerifiableBackup()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordType person = await records.CreateRecordTypeAsync("Backed-up person");
            _ = await records.CreateRecordAsync(person.Id, "Ada", []);
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(
            factory, ["instance.read", "backups.read", "backups.write"]);

        using JsonDocument before = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_operational_status");
        RemoteOperationalStatus status = Structured(before).Deserialize<RemoteOperationalStatus>(JsonOptions)!;
        Assert.True(status.Ready);
        Assert.Equal(1, status.DomainCount);
        Assert.Equal(0, status.BackupCount);
        Assert.Null(status.NewestBackupAtUtc);

        // The schedule is reported and off by default, with no next run to report while it is off.
        Assert.Equal("Off", status.Schedule.Frequency);
        Assert.Null(status.Schedule.NextRunAtUtc);
        Assert.Equal(12, status.Schedule.RetentionCount);

        // Nothing here describes the machine. The assertion is over the whole response because that is
        // the property, not any one field.
        Assert.DoesNotContain("C:\\", Structured(before).GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("/var/", Structured(before).GetRawText(), StringComparison.Ordinal);

        using JsonDocument empty = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_backups");
        Assert.Empty(Structured(empty).Deserialize<RemoteBackup[]>(JsonOptions)!);

        using JsonDocument created = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_backup");
        RemoteBackup backup = Structured(created).Deserialize<RemoteBackup>(JsonOptions)!;
        Assert.NotEqual(Guid.Empty, backup.Id);
        Assert.True(backup.ByteLength > 0);

        // A successful create is unambiguous on its own, and list_backups is how a caller reconciles a
        // lost response: the package is there, once, with the creation time the create reported.
        RemoteBackup listed = Assert.Single(
            Structured(await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_backups"))
                .Deserialize<RemoteBackup[]>(JsonOptions)!);
        Assert.Equal(backup.Id, listed.Id);
        Assert.Equal(backup.CreatedAtUtc, listed.CreatedAtUtc);

        // Validation opens the package rather than trusting its size, which is what makes it worth
        // having: it reports the schema the backup was taken at, so a restorable one is distinguishable.
        using JsonDocument validated = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "validate_backup", new { id = backup.Id });
        RemoteBackupValidation validation = Structured(validated).Deserialize<RemoteBackupValidation>(JsonOptions)!;
        Assert.Equal(backup.Id, validation.Backup.Id);
        Assert.True(validation.EntryCount > 0);
        Assert.Equal(Monkeysphere.Data.MonkeysphereSchema.Manifest.CurrentVersion, validation.ApplicationSchemaVersion);

        using JsonDocument after = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_operational_status");
        RemoteOperationalStatus updated = Structured(after).Deserialize<RemoteOperationalStatus>(JsonOptions)!;
        Assert.Equal(1, updated.BackupCount);
        Assert.Equal(backup.CreatedAtUtc, updated.NewestBackupAtUtc);

        using JsonDocument missing = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "validate_backup", new { id = Guid.CreateVersion7() });
        AssertWriteError(missing, "not_found");
    }

    [Theory]
    [InlineData("backups.read", true, false)]
    [InlineData("backups.write", false, true)]
    [InlineData("records.read", false, false)]
    public async Task BackupGrantsAreSeparableFromEachOtherAndFromReadingRecords(string grant, bool canRead, bool canWrite)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordType person = await records.CreateRecordTypeAsync("Person");
            _ = await records.CreateRecordAsync(person.Id, "Secret person", []);
            _ = await scope.ServiceProvider.GetRequiredService<IBackupService>().CreateAsync();
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);

        using JsonDocument listed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_backups");
        if (canRead)
        {
            // Reading backup metadata is not reading records: the package covers every domain, and what
            // comes back is its size and shape, never a record's name.
            RemoteBackup only = Assert.Single(Structured(listed).Deserialize<RemoteBackup[]>(JsonOptions)!);
            Assert.True(only.ByteLength > 0);
            Assert.DoesNotContain("Secret person", Structured(listed).GetRawText(), StringComparison.Ordinal);
        }
        else
        {
            AssertWriteError(listed, "permission_denied");
        }

        using JsonDocument created = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_backup");
        if (canWrite) _ = Structured(created); else AssertWriteError(created, "permission_denied");

        // Taking a backup is not reading one, and neither is reading records: the three are separate
        // grants because a credential that can take a package should not thereby be able to inspect it.
        using JsonDocument validated = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "validate_backup", new { id = Guid.CreateVersion7() });
        if (!canRead) AssertWriteError(validated, "permission_denied");

        // And no backup grant is a way into the records themselves.
        using JsonDocument structures = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "query_record_types", new { domainId = MonkeysphereDomains.DefaultId });
        if (grant != "records.read") AssertWriteError(structures, "permission_denied");
    }

    [Fact]
    public async Task McpReadsABackupInRangesThatReassembleToTheStoredPackage()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();

        BackupInfo stored;
        byte[] onDisk;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordType person = await records.CreateRecordTypeAsync("Downloaded person");
            _ = await records.CreateRecordAsync(person.Id, "Ada", []);
            IBackupService backups = scope.ServiceProvider.GetRequiredService<IBackupService>();
            stored = await backups.CreateAsync();
            await using Stream source = (await backups.OpenAsync(stored.Id))!;
            using MemoryStream copy = new();
            await source.CopyToAsync(copy);
            onDisk = copy.ToArray();
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["backups.export"]);

        // Deliberately small ranges, so the paging is exercised rather than the whole package arriving
        // in one chunk and the offsets never being tested.
        List<byte> rebuilt = [];
        long? offset = 0;
        string? digest = null;
        int chunks = 0;
        while (offset is long at)
        {
            using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
                "read_backup", new { id = stored.Id, offset = at, count = 4096, includeDigest = chunks == 0 });
            RemoteBackupContent chunk = Structured(response).Deserialize<RemoteBackupContent>(JsonOptions)!;
            Assert.Equal(stored.Id, chunk.BackupId);
            Assert.Equal(onDisk.LongLength, chunk.TotalBytes);
            Assert.Equal(at, chunk.Offset);
            if (chunks == 0) digest = chunk.ContentDigest;

            // The digest is opt-in, so every chunk after the first omits it rather than re-reading the
            // whole package to recompute something that cannot have changed.
            else Assert.Null(chunk.ContentDigest);
            rebuilt.AddRange(Convert.FromBase64String(chunk.ContentBase64));
            offset = chunk.NextOffset;
            chunks++;
        }

        Assert.True(chunks > 1, $"the package arrived in {chunks} chunk(s), so paging was never exercised");
        Assert.Equal(onDisk, rebuilt.ToArray());
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(onDisk)), digest);

        // The end offset is an empty final range; past it is a caller error, not an empty success.
        using JsonDocument end = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "read_backup", new { id = stored.Id, offset = onDisk.LongLength, count = 4096 });
        RemoteBackupContent last = Structured(end).Deserialize<RemoteBackupContent>(JsonOptions)!;
        Assert.Equal(string.Empty, last.ContentBase64);
        Assert.Null(last.NextOffset);

        using JsonDocument beyond = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "read_backup", new { id = stored.Id, offset = onDisk.LongLength + 1, count = 4096 });
        AssertWriteError(beyond, "validation_failed");

        using JsonDocument oversized = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "read_backup", new { id = stored.Id, offset = 0, count = 65537 });
        AssertWriteError(oversized, "validation_failed");

        using JsonDocument absent = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "read_backup", new { id = Guid.CreateVersion7(), offset = 0, count = 4096 });
        AssertWriteError(absent, "not_found");
    }

    [Fact]
    public async Task ReadingABackupNeedsItsOwnGrantAndIsNotImpliedByListingOrTakingOne()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();

        BackupInfo stored;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            stored = await scope.ServiceProvider.GetRequiredService<IBackupService>().CreateAsync();
        }

        // Holding both of the other backup grants is still not permission to take a copy of everything
        // out of the deployment. That separation is the whole reason backups.export exists.
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["backups.read", "backups.write"]);
        using JsonDocument refused = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call",
            "read_backup", new { id = stored.Id, offset = 0, count = 4096 });
        AssertWriteError(refused, "permission_denied");
    }

    [Fact]
    public async Task TheExportGrantAloneCannotListOrTakeBackups()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            _ = await scope.ServiceProvider.GetRequiredService<IBackupService>().CreateAsync();
        }

        // The separation runs both ways: being trusted to read a package out is not being trusted to
        // discover what packages exist, or to make the deployment do the work of writing a new one.
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["backups.export"]);
        using JsonDocument listed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_backups");
        AssertWriteError(listed, "permission_denied");
        using JsonDocument created = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_backup");
        AssertWriteError(created, "permission_denied");
    }

    [Fact]
    public async Task OperationalStatusNeedsInstanceReadAndIsNotImpliedByABackupGrant()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["backups.read"]);

        // Holding a backup grant says nothing about being allowed to ask how the deployment is doing.
        // The two are reported separately by discovery, so they are enforced separately here.
        using JsonDocument refused = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_operational_status");
        AssertWriteError(refused, "permission_denied");
    }
}
