using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

public sealed partial class DomainIsolationTests
{
    [Theory]
    [InlineData("domain", 7)]
    [InlineData("global", 63)]
    [InlineData("bytes", 13)]
    [InlineData("retention", 999)]
    public async Task UploadGlobalQuotasPreserveExistingRetryAccess(string dimension, int count)
    {
        string root = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using ServiceProvider provider = RegistryProvider(root);
            await provider.InitializeMonkeysphereDomainsAsync();
            IRemoteUploadStore store = provider.GetRequiredService<IRemoteUploadStore>();
            UploadOwner owner = new(MonkeysphereDomains.DefaultId, new string('A', 64));
            UploadRequest original = new(UploadPurposes.ContactImport, 1, new string('0', 64), "text/vcard");
            Guid key = Guid.NewGuid();
            UploadStatus existing = await store.BeginAsync(owner, key, original);
            await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, RemoteUploadSchema.FileName) }.ConnectionString);
            await connection.OpenAsync();
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                for (int index = 0; index < count; index++)
                {
                    await connection.ExecuteAsync("""
                        INSERT INTO UploadSessions (Id, DomainId, CredentialFingerprint, IdempotencyKey, Purpose, ByteLength, Sha256, ContentType,
                            State, CreatedAtUtc, ExpiresAtUtc, RetryUntilUtc, ForgetAfterUtc)
                        VALUES (@Id, @DomainId, @Fingerprint, @Key, 'contact_import', @Length, @Hash, 'text/vcard', @State, @Now, @Future, @Future, @Future);
                        """, new
                    {
                        Id = Guid.NewGuid().ToString("D"),
                        DomainId = (dimension == "domain" ? owner.DomainId : Guid.NewGuid()).ToString("D"),
                        Fingerprint = new string('B', 64),
                        Key = Guid.NewGuid().ToString("D"),
                        Length = dimension == "bytes" ? (index == 12 ? 3 : 5) * 1024 * 1024 : 1,
                        Hash = new string('0', 64),
                        State = dimension == "retention" ? "cancelled" : "receiving",
                        Now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                        Future = DateTimeOffset.UtcNow.AddDays(10).ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                    }, transaction);
                }
                await transaction.CommitAsync();
            }
            UploadException denied = await Assert.ThrowsAsync<UploadException>(() => store.BeginAsync(owner, Guid.NewGuid(), original with { ByteLength = 5 * 1024 * 1024 }));
            Assert.Equal("limit_exceeded", denied.Code);
            Assert.Equal(existing, await store.BeginAsync(owner, key, original));
            Assert.Equal(count + 1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM UploadSessions;"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UploadChunkCountIsBoundedAndMetadataTombstonesExpire()
    {
        string root = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using ServiceProvider provider = RegistryProvider(root);
            await provider.InitializeMonkeysphereDomainsAsync();
            IRemoteUploadStore store = provider.GetRequiredService<IRemoteUploadStore>();
            UploadOwner owner = new(MonkeysphereDomains.DefaultId, new string('A', 64));
            UploadRequest request = new(UploadPurposes.ContactImport, RemoteUploadLimits.MaximumChunksPerUpload + 1, new string('0', 64), "text/vcard");
            Guid key = Guid.NewGuid();
            UploadStatus begun = await store.BeginAsync(owner, key, request);
            byte[] chunk = [1];
            string hash = Convert.ToHexString(SHA256.HashData(chunk));
            for (int index = 0; index < RemoteUploadLimits.MaximumChunksPerUpload; index++)
                _ = await store.WriteAsync(owner, begun.UploadId, index, chunk, hash);
            Assert.Equal("limit_exceeded", (await Assert.ThrowsAsync<UploadException>(() => store.WriteAsync(owner, begun.UploadId, RemoteUploadLimits.MaximumChunksPerUpload, chunk, hash))).Code);
            Assert.Equal(RemoteUploadLimits.MaximumChunksPerUpload, (await store.WriteAsync(owner, begun.UploadId, 0, chunk, hash)).AcceptedBytes);
            _ = await store.CancelAsync(owner, begun.UploadId);
            await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, RemoteUploadSchema.FileName) }.ConnectionString);
            await connection.OpenAsync();
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM UploadChunks;"));
            await connection.ExecuteAsync("UPDATE UploadSessions SET RetryUntilUtc = '2000-01-01T00:00:00.0000000+00:00';");
            Assert.Equal("retry_expired", (await Assert.ThrowsAsync<UploadException>(() => store.BeginAsync(owner, key, request))).Code);
            await connection.ExecuteAsync("UPDATE UploadSessions SET ForgetAfterUtc = '2000-01-01T00:00:00.0000000+00:00';");
            await store.CleanupAsync();
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM UploadSessions;"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UploadChunksReplayAtomicallyAndSurviveRestartWithOwnerIsolation()
    {
        string root = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        UploadOwner owner = new(MonkeysphereDomains.DefaultId, new string('A', 64));
        byte[] bytes = RandomNumberGenerator.GetBytes(RemoteUploadLimits.MaximumChunkBytes + 37);
        byte[] first = bytes[..RemoteUploadLimits.MaximumChunkBytes];
        UploadRequest request = new(UploadPurposes.ContactImport, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "text/vcard");
        Guid retryKey = Guid.NewGuid();
        Guid uploadId;
        try
        {
            await using (ServiceProvider provider = RegistryProvider(root))
            {
                await provider.InitializeMonkeysphereDomainsAsync();
                IRemoteUploadStore store = provider.GetRequiredService<IRemoteUploadStore>();
                UploadStatus begun = await store.BeginAsync(owner, retryKey, request);
                uploadId = begun.UploadId;
                Assert.Equal(begun, await store.BeginAsync(owner, retryKey, request));
                Assert.Equal(begun, await store.BeginAsync(owner, retryKey, request with { MaximumChunkBytes = 0 }));
                await Assert.ThrowsAsync<UploadException>(() => store.BeginAsync(owner, retryKey, request with { ByteLength = bytes.Length - 1 }));
                await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, RemoteUploadSchema.FileName) }.ConnectionString);
                await connection.OpenAsync();
                await connection.ExecuteAsync("CREATE TRIGGER TestRejectOffset BEFORE UPDATE OF AcceptedBytes ON UploadSessions BEGIN SELECT RAISE(ABORT, 'Test failure'); END;");
                await Assert.ThrowsAsync<SqliteException>(() => store.WriteAsync(owner, uploadId, 0, first, Convert.ToHexString(SHA256.HashData(first))));
                Assert.Equal(0, (await store.GetAsync(owner, uploadId)).AcceptedBytes);
                Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM UploadChunks;"));
                await connection.ExecuteAsync("DROP TRIGGER TestRejectOffset;");
                UploadStatus[] writes = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => store.WriteAsync(owner, uploadId, 0, first, Convert.ToHexString(SHA256.HashData(first))))));
                Assert.Equal(writes[0], writes[1]);
                Assert.Equal(first.Length, writes[0].AcceptedBytes);
                await Assert.ThrowsAsync<DomainValidationException>(() => store.WriteAsync(owner, uploadId, first.Length + 1, [1], Convert.ToHexString(SHA256.HashData(new byte[] { 1 }))));
                await Assert.ThrowsAsync<UploadException>(() => store.WriteAsync(owner, uploadId, 1, [1], Convert.ToHexString(SHA256.HashData(new byte[] { 1 }))));
                await Assert.ThrowsAsync<DomainValidationException>(() => store.SealAsync(owner, uploadId));
                Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM UploadChunks;"));
                MonkeysphereDomain other = await provider.GetRequiredService<IDomainCatalog>().CreateAsync("Other upload domain");
                foreach (UploadOwner foreign in new[] { owner with { CredentialFingerprint = new string('B', 64) }, owner with { DomainId = other.Id } })
                {
                    Assert.Equal("not_found", (await Assert.ThrowsAsync<UploadException>(() => store.GetAsync(foreign, uploadId))).Code);
                    await Assert.ThrowsAsync<UploadException>(() => store.CancelAsync(foreign, uploadId));
                }
            }
            await using (ServiceProvider provider = RegistryProvider(root))
            {
                await provider.InitializeMonkeysphereDomainsAsync();
                IRemoteUploadStore store = provider.GetRequiredService<IRemoteUploadStore>();
                Assert.Equal(first.Length, (await store.BeginAsync(owner, retryKey, request)).AcceptedBytes);
                byte[] tail = bytes[first.Length..];
                _ = await store.WriteAsync(owner, uploadId, first.Length, tail, Convert.ToHexString(SHA256.HashData(tail)));
                UploadStatus sealedUpload = await store.SealAsync(owner, uploadId);
                Assert.Equal("sealed", sealedUpload.State);
                Assert.Equal(sealedUpload, await store.SealAsync(owner, uploadId));
                Assert.Equal(sealedUpload, await store.WriteAsync(owner, uploadId, 0, first, Convert.ToHexString(SHA256.HashData(first))));
                using MemoryStream output = new();
                await store.CopySealedToAsync(owner, uploadId, output);
                Assert.Equal(bytes, output.ToArray());
                Assert.Equal("cancelled", (await store.CancelAsync(owner, uploadId)).State);
                Assert.Equal("cancelled", (await store.BeginAsync(owner, retryKey, request)).State);
                await Assert.ThrowsAsync<UploadException>(() => store.CopySealedToAsync(owner, uploadId, Stream.Null));
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UploadQuotasChecksumsExpiryAndCancellationFailWithoutPartialBytes()
    {
        string root = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using ServiceProvider provider = RegistryProvider(root);
            await provider.InitializeMonkeysphereDomainsAsync();
            IRemoteUploadStore store = provider.GetRequiredService<IRemoteUploadStore>();
            UploadOwner owner = new(MonkeysphereDomains.DefaultId, new string('A', 64));
            byte[] bytes = [1, 2, 3];
            string digest = Convert.ToHexString(SHA256.HashData(bytes));
            UploadRequest request = new(UploadPurposes.ContactImport, bytes.Length, digest, "text/vcard");
            List<UploadStatus> sessions = [];
            for (int index = 0; index < RemoteUploadLimits.MaximumActiveSessionsPerOwner; index++)
                sessions.Add(await store.BeginAsync(owner, Guid.NewGuid(), request));
            Assert.Equal("limit_exceeded", (await Assert.ThrowsAsync<UploadException>(() => store.BeginAsync(owner, Guid.NewGuid(), request))).Code);
            UploadStatus upload = sessions[0];
            await Assert.ThrowsAsync<DomainValidationException>(() => store.WriteAsync(owner, upload.UploadId, 0, bytes, new string('0', 64)));
            Assert.Equal(0, (await store.GetAsync(owner, upload.UploadId)).AcceptedBytes);
            using CancellationTokenSource cancelled = new();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(owner, upload.UploadId, 0, bytes, digest, cancelled.Token));
            _ = await store.WriteAsync(owner, upload.UploadId, 0, bytes, digest);
            await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, RemoteUploadSchema.FileName) }.ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("UPDATE UploadSessions SET Sha256 = @Digest WHERE Id = @Id;", new { Digest = new string('F', 64), Id = upload.UploadId.ToString("D") });
            await Assert.ThrowsAsync<DomainValidationException>(() => store.SealAsync(owner, upload.UploadId));
            Assert.Equal("receiving", (await store.GetAsync(owner, upload.UploadId)).State);
            await connection.ExecuteAsync("UPDATE UploadSessions SET ExpiresAtUtc = '2000-01-01T00:00:00.0000000+00:00';");
            Assert.Equal("expired", (await store.GetAsync(owner, upload.UploadId)).State);
            Assert.Equal("upload_expired", (await Assert.ThrowsAsync<UploadException>(() => store.SealAsync(owner, upload.UploadId))).Code);
            await store.CleanupAsync();
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM UploadChunks;"));
            Assert.Equal(sessions.Count, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM UploadSessions;"));
            _ = await store.BeginAsync(owner, Guid.NewGuid(), request);
            await Assert.ThrowsAsync<DomainValidationException>(() => store.BeginAsync(owner, Guid.NewGuid(), request with { ByteLength = RemoteUploadLimits.MaximumContactBytes + 1 }));
            await Assert.ThrowsAsync<DomainValidationException>(() => store.BeginAsync(owner, Guid.NewGuid(), request with { ContentType = "application/octet-stream" }));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
