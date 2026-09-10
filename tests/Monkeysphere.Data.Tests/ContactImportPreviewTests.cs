using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

public sealed partial class DomainIsolationTests
{
    [Theory]
    [InlineData("domain", 7)]
    [InlineData("global", 31)]
    [InlineData("bytes", 4)]
    [InlineData("retained", 999)]
    public async Task ContactPreviewDeploymentQuotasKeepExistingPreviewReadable(string dimension, int count)
    {
        string root = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using ServiceProvider provider = RegistryProvider(root);
            await provider.InitializeMonkeysphereDomainsAsync();
            using IServiceScope scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
            UploadOwner owner = new(MonkeysphereDomains.DefaultId, new string('E', 64));
            byte[] bytes = Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nFN:Deployment quota fixture\nEND:VCARD\n");
            Guid upload = await StagePreviewUploadAsync(provider.GetRequiredService<IRemoteUploadStore>(), owner, bytes);
            ContactImportPreviewService service = scope.ServiceProvider.GetRequiredService<ContactImportPreviewService>();
            Guid key = Guid.NewGuid();
            ContactImportPreviewHandle first = await service.CreateAsync(owner, upload, key);
            await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, RemoteUploadSchema.FileName) }.ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                WITH RECURSIVE Numbers(N) AS (SELECT 1 UNION ALL SELECT N + 1 FROM Numbers WHERE N < @Count)
                INSERT INTO ContactImportPreviews (Id, UploadId, DomainId, CredentialFingerprint, IdempotencyKey, RecordTypeId,
                    RecordTypeName, Revision, ContactCount, StoredBytes, ExpiresAtUtc, ForgetAfterUtc)
                SELECT 'quota-' || N, p.UploadId,
                    CASE WHEN @Dimension = 'domain' THEN p.DomainId ELSE 'domain-' || N END,
                    'credential-' || N, 'key-' || N, p.RecordTypeId, p.RecordTypeName, p.Revision, 1,
                    CASE WHEN @Dimension = 'bytes' THEN 33554432 WHEN @Dimension = 'retained' THEN 0 ELSE 1 END,
                    p.ExpiresAtUtc, p.ForgetAfterUtc
                FROM Numbers CROSS JOIN ContactImportPreviews p WHERE p.Id = @Id;
                """, new { Count = count, Dimension = dimension, Id = first.PreviewId.ToString("D") });
            Assert.Equal("limit_exceeded", (await Assert.ThrowsAsync<UploadException>(() => service.CreateAsync(owner, upload, Guid.NewGuid()))).Code);
            Assert.Equal(first, await service.CreateAsync(owner, upload, key));
            Assert.Single(await provider.GetRequiredService<IContactImportPreviewStore>().ReadContactsAsync(owner, first.PreviewId, 1, 1));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ContactPreviewPersistsCompleteMappingAndReplaysAcrossRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            UploadOwner owner = new(MonkeysphereDomains.DefaultId, new string('A', 64));
            Guid key = Guid.NewGuid();
            ContactImportPreviewHandle handle;
            string originalJson;
            await using (ServiceProvider provider = RegistryProvider(root))
            {
                await provider.InitializeMonkeysphereDomainsAsync();
                using IServiceScope scope = provider.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
                IVCardService vcards = scope.ServiceProvider.GetRequiredService<IVCardService>();
                byte[] bytes = Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nFN:Preview fixture\nEMAIL:preview@example.test\nitem1.TEL:+441234567890\nitem1.X-ABLabel:Custom label\nX-OPAQUE;X-PARAM=value:kept\\,value\nEND:VCARD\nBEGIN:VCARD\nVERSION:4.0\nFN:Preview fixture\nEMAIL:preview@example.test\nEND:VCARD\n");
                VCardImportPreview browser = await vcards.PreviewAsync(bytes);
                _ = await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>().CreateRecordAsync(browser.RecordTypeId, "Preview fixture", [], ["Preview alias"]);
                browser = await vcards.PreviewAsync(bytes);
                originalJson = JsonSerializer.Serialize(browser.Contacts);
                IRemoteUploadStore uploads = provider.GetRequiredService<IRemoteUploadStore>();
                Guid uploadId = await StagePreviewUploadAsync(uploads, owner, bytes);
                ContactImportPreviewService service = scope.ServiceProvider.GetRequiredService<ContactImportPreviewService>();
                handle = await service.CreateAsync(owner, uploadId, key);
                Assert.Equal(browser.Revision, handle.Revision);
                Assert.Equal(2, handle.ContactCount);
                Assert.Equal(handle, await service.CreateAsync(owner, uploadId, key));
                IContactImportPreviewStore previews = provider.GetRequiredService<IContactImportPreviewStore>();
                Assert.Equal(originalJson, JsonSerializer.Serialize(await previews.ReadContactsAsync(owner, handle.PreviewId, 1, 100)));
                Assert.Single(await previews.ReadContactsAsync(owner, handle.PreviewId, 2, 1));
                Assert.Empty(await previews.ReadContactsAsync(owner, handle.PreviewId, 3, 1));
                await Assert.ThrowsAsync<DomainValidationException>(() => previews.ReadContactsAsync(owner, handle.PreviewId, 1, 101));
                await Assert.ThrowsAsync<UploadException>(() => previews.GetAsync(owner with { CredentialFingerprint = new string('B', 64) }, handle.PreviewId));
                MonkeysphereDomain other = await provider.GetRequiredService<IDomainCatalog>().CreateAsync("Other preview domain");
                await Assert.ThrowsAsync<UploadException>(() => previews.GetAsync(owner with { DomainId = other.Id }, handle.PreviewId));
                await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(owner with { DomainId = other.Id }, uploadId, Guid.NewGuid()));
                Assert.Equal("retry_conflict", (await Assert.ThrowsAsync<UploadException>(() => previews.FindAsync(owner, Guid.NewGuid(), key))).Code);
            }
            await using (ServiceProvider provider = RegistryProvider(root))
            {
                await provider.InitializeMonkeysphereDomainsAsync();
                IContactImportPreviewStore previews = provider.GetRequiredService<IContactImportPreviewStore>();
                Assert.Equal(handle, await previews.FindAsync(owner, handle.UploadId, key));
                IReadOnlyList<VCardContactPreview> contacts = await previews.ReadContactsAsync(owner, handle.PreviewId, 1, 100);
                Assert.Equal(originalJson, JsonSerializer.Serialize(contacts));
                using IServiceScope scope = provider.CreateScope();
                IVCardService vcards = scope.ServiceProvider.GetRequiredService<IVCardService>();
                VCardImportPreview restored = new(handle.RecordTypeId, handle.RecordTypeName, contacts, handle.Revision);
                Assert.Equal(2, (await vcards.ApplyAsync(restored, [new(0, VCardImportAction.CreateSeparately), new(1, VCardImportAction.CreateSeparately)])).Created);
                // A retry retains the reviewed evidence even after records change; apply owns the revision check.
                Assert.Equal(handle, await scope.ServiceProvider.GetRequiredService<ContactImportPreviewService>().CreateAsync(owner, handle.UploadId, key));
                await Assert.ThrowsAsync<ConcurrencyConflictException>(() => vcards.ApplyAsync(restored, [new(0, VCardImportAction.Skip), new(1, VCardImportAction.Skip)]));
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ContactPreviewQuotaExpiryAndCancellationPreserveRetryTombstones()
    {
        string root = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using ServiceProvider provider = RegistryProvider(root);
            await provider.InitializeMonkeysphereDomainsAsync();
            using IServiceScope scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
            IRemoteUploadStore uploads = provider.GetRequiredService<IRemoteUploadStore>();
            IContactImportPreviewStore previews = provider.GetRequiredService<IContactImportPreviewStore>();
            ContactImportPreviewService service = scope.ServiceProvider.GetRequiredService<ContactImportPreviewService>();
            UploadOwner owner = new(MonkeysphereDomains.DefaultId, new string('C', 64));
            Guid upload = await StagePreviewUploadAsync(uploads, owner, Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nFN:Quota fixture\nEND:VCARD\n"));
            Guid key = Guid.NewGuid();
            ContactImportPreviewHandle first = await service.CreateAsync(owner, upload, key);
            for (int index = 1; index < ContactImportPreviewLimits.MaximumActivePerOwner; index++)
                _ = await service.CreateAsync(owner, upload, Guid.NewGuid());
            Assert.Equal("limit_exceeded", (await Assert.ThrowsAsync<UploadException>(() => service.CreateAsync(owner, upload, Guid.NewGuid()))).Code);
            Assert.Equal(first, await service.CreateAsync(owner, upload, key));
            await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, RemoteUploadSchema.FileName) }.ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("UPDATE ContactImportPreviews SET ExpiresAtUtc = '2000-01-01T00:00:00.0000000+00:00';");
            Assert.Equal("preview_expired", (await Assert.ThrowsAsync<UploadException>(() => previews.GetAsync(owner, first.PreviewId))).Code);
            await uploads.CleanupAsync();
            Assert.Equal(0, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM ContactImportPreviewContacts;"));
            Assert.Equal(4, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM ContactImportPreviews;"));
            Assert.Equal("preview_expired", (await Assert.ThrowsAsync<UploadException>(() => service.CreateAsync(owner, upload, key))).Code);
            ContactImportPreviewHandle fresh = await service.CreateAsync(owner, upload, Guid.NewGuid());
            _ = await uploads.CancelAsync(owner, upload);
            await Assert.ThrowsAsync<UploadException>(() => previews.ReadContactsAsync(owner, fresh.PreviewId, 1, 100));
            Assert.Equal(0, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM ContactImportPreviewContacts;"));
            Assert.Equal(0, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM ContactImportPreviews WHERE RecordTypeName <> '' OR Revision <> '';"));
            await connection.ExecuteAsync("UPDATE ContactImportPreviews SET ForgetAfterUtc = '2000-01-01T00:00:00.0000000+00:00';");
            await uploads.CleanupAsync();
            Assert.Equal(0, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM ContactImportPreviews;"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ContactPreviewAuditFailureRollsBackAndConcurrentRetriesShareIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using ServiceProvider provider = RegistryProvider(root);
            await provider.InitializeMonkeysphereDomainsAsync();
            using IServiceScope scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
            UploadOwner owner = new(MonkeysphereDomains.DefaultId, new string('D', 64));
            byte[] bytes = Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nFN:Atomic fixture\nEND:VCARD\n");
            Guid upload = await StagePreviewUploadAsync(provider.GetRequiredService<IRemoteUploadStore>(), owner, bytes);
            _ = await provider.GetRequiredService<IRemoteUploadStore>().SealAsync(owner, upload);
            VCardImportPreview preview = await scope.ServiceProvider.GetRequiredService<IVCardService>().PreviewAsync(bytes);
            IContactImportPreviewStore previews = provider.GetRequiredService<IContactImportPreviewStore>();
            await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, RemoteUploadSchema.FileName) }.ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("CREATE TRIGGER TestFailPreviewAudit BEFORE INSERT ON UploadAudit WHEN NEW.Action = 'contacts.preview' BEGIN SELECT RAISE(ABORT, 'Test failure'); END;");
            Guid key = Guid.NewGuid();
            await Assert.ThrowsAsync<SqliteException>(() => previews.SaveAsync(owner, upload, key, preview));
            Assert.Equal(0, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM ContactImportPreviews;"));
            Assert.Equal(0, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM ContactImportPreviewContacts;"));
            await connection.ExecuteAsync("DROP TRIGGER TestFailPreviewAudit;");
            ContactImportPreviewHandle[] results = await Task.WhenAll(Task.Run(() => previews.SaveAsync(owner, upload, key, preview)), Task.Run(() => previews.SaveAsync(owner, upload, key, preview)));
            Assert.Equal(results[0], results[1]);
            Assert.Equal(1, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM UploadAudit WHERE Action = 'contacts.preview';"));
            // Reject oversized evidence before persistence without truncating it.
            VCardContactPreview large = preview.Contacts[0] with { DisplayName = new string('x', ContactImportPreviewLimits.MaximumBytes) };
            Assert.Equal("limit_exceeded", (await Assert.ThrowsAsync<UploadException>(() => previews.SaveAsync(owner, upload, Guid.NewGuid(), preview with { Contacts = [large] }))).Code);
            Assert.Equal(1, await connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM ContactImportPreviews;"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static async Task<Guid> StagePreviewUploadAsync(IRemoteUploadStore uploads, UploadOwner owner, byte[] bytes)
    {
        UploadStatus status = await uploads.BeginAsync(owner, Guid.NewGuid(), new(UploadPurposes.ContactImport, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "text/vcard"));
        _ = await uploads.WriteAsync(owner, status.UploadId, 0, bytes, Convert.ToHexString(SHA256.HashData(bytes)));
        return status.UploadId;
    }
}
