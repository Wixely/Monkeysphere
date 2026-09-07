using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

public sealed partial class DomainIsolationTests
{
    [Fact]
    public async Task UploadAuditFailureRollsBackSessionAndChunkMutations()
    {
        string root = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using ServiceProvider provider = RegistryProvider(root);
            await provider.InitializeMonkeysphereDomainsAsync();
            IRemoteUploadStore uploads = provider.GetRequiredService<IRemoteUploadStore>();
            UploadOwner owner = new(MonkeysphereDomains.DefaultId, new string('A', 64), "test-correlation");
            byte[] bytes = [1, 2, 3];
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, RemoteUploadSchema.FileName) }.ConnectionString);
            await connection.OpenAsync();
            const string reject = "CREATE TRIGGER TestRejectUploadAudit BEFORE INSERT ON UploadAudit BEGIN SELECT RAISE(ABORT, 'Test failure'); END;";
            await connection.ExecuteAsync(reject);
            await Assert.ThrowsAsync<SqliteException>(() => uploads.BeginAsync(owner, Guid.NewGuid(), new(3, hash, "text/vcard")));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM UploadSessions;"));
            await connection.ExecuteAsync("DROP TRIGGER TestRejectUploadAudit;");
            UploadStatus begun = await uploads.BeginAsync(owner, Guid.NewGuid(), new(3, hash, "text/vcard"));
            await connection.ExecuteAsync(reject);
            await Assert.ThrowsAsync<SqliteException>(() => uploads.WriteAsync(owner, begun.UploadId, 0, bytes, hash));
            Assert.Equal(0, (await uploads.GetAsync(owner, begun.UploadId)).AcceptedBytes);
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM UploadChunks;"));
            await Assert.ThrowsAsync<SqliteException>(() => uploads.CancelAsync(owner, begun.UploadId));
            Assert.Equal("receiving", (await uploads.GetAsync(owner, begun.UploadId)).State);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
