using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Data;

namespace Monkeysphere.Web.Tests;

public sealed class UploadBackupTests
{
    [Fact]
    public async Task BackupExcludesStagedUploadsAndRestoreInvalidatesTheirHandles()
    {
        string root = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        UploadOwner owner = new(MonkeysphereDomains.DefaultId, new string('A', 64));
        try
        {
            string package;
            Guid uploadId;
            await using (PersistentApplicationFactory factory = new(root))
            {
                IRemoteUploadStore uploads = factory.Services.GetRequiredService<IRemoteUploadStore>();
                byte[] bytes = [1, 2, 3];
                string digest = Convert.ToHexString(SHA256.HashData(bytes));
                UploadStatus begun = await uploads.BeginAsync(owner, Guid.NewGuid(), new(bytes.Length, digest, "text/vcard"));
                uploadId = begun.UploadId;
                _ = await uploads.WriteAsync(owner, uploadId, 0, bytes, digest);
                using IServiceScope scope = factory.Services.CreateScope();
                IBackupService backups = scope.ServiceProvider.GetRequiredService<IBackupService>();
                BackupInfo backup = await backups.CreateAsync();
                _ = await backups.ValidateAsync(backup.Id);
                package = Path.Combine(root, "backups", backup.FileName);
                using ZipArchive archive = ZipFile.OpenRead(package);
                Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("remote-transfers", StringComparison.Ordinal));
            }
            SqliteConnection.ClearAllPools();
            string rollback = await OfflineBackupRestore.RestoreAsync(package, root);
            Assert.False(File.Exists(Path.Combine(root, RemoteUploadSchema.FileName)));
            Assert.Empty(Directory.GetFiles(rollback, "remote-transfers.db*"));
            await using (PersistentApplicationFactory factory = new(root))
            {
                IRemoteUploadStore uploads = factory.Services.GetRequiredService<IRemoteUploadStore>();
                Assert.Equal("not_found", (await Assert.ThrowsAsync<UploadException>(() => uploads.GetAsync(owner, uploadId))).Code);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
