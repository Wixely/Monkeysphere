using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

public sealed partial class DomainIsolationTests
{
    [Fact]
    public async Task ContactValidationReadsStagedChunksAndDoesNotImportOrTrustMalformedContent()
    {
        string root = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using ServiceProvider provider = RegistryProvider(root);
            await provider.InitializeMonkeysphereDomainsAsync();
            IRemoteUploadStore uploads = provider.GetRequiredService<IRemoteUploadStore>();
            ContactUploadService validator = provider.GetRequiredService<ContactUploadService>();
            UploadOwner owner = new(MonkeysphereDomains.DefaultId, new string('A', 64));
            async Task<UploadStatus> StageAsync(byte[] bytes)
            {
                UploadStatus begun = await uploads.BeginAsync(owner, Guid.NewGuid(), new(UploadPurposes.ContactImport, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "text/vcard"));
                for (int offset = 0; offset < bytes.Length; offset += 7)
                {
                    byte[] chunk = bytes[offset..Math.Min(offset + 7, bytes.Length)];
                    _ = await uploads.WriteAsync(owner, begun.UploadId, offset, chunk, Convert.ToHexString(SHA256.HashData(chunk)));
                }
                return begun;
            }
            byte[] valid = Encoding.UTF8.GetBytes("BEGIN:VCARD\r\nVERSION:4.0\r\nFN:Zoë Example\r\nX-EXAMPLE:one\r\n two\r\nEND:VCARD\r\n");
            UploadStatus staged = await StageAsync(valid);
            Assert.Equal("upload_not_ready", (await Assert.ThrowsAsync<UploadException>(() => uploads.ReadSealedAsync(owner, staged.UploadId, VCardParser.ParseAsync))).Code);
            IReadOnlyList<VCard> cards = await validator.ValidateAsync(owner, staged.UploadId);
            Assert.Equal(Assert.Single(VCardParser.Parse(valid)).Fingerprint, Assert.Single(cards).Fingerprint);
            Assert.Equal(cards[0].Fingerprint, Assert.Single(await validator.ValidateAsync(owner, staged.UploadId)).Fingerprint);
            await Assert.ThrowsAsync<UploadException>(() => validator.ValidateAsync(owner with { CredentialFingerprint = new string('B', 64) }, staged.UploadId));
            using IServiceScope scope = provider.CreateScope();
            Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>().SearchRecordsAsync(new())).TotalCount);
            await Assert.ThrowsAsync<InvalidOperationException>(() => uploads.ReadSealedAsync(owner, staged.UploadId, (_, _) => Task.FromResult(0)));
            Stream? retained = null;
            _ = await uploads.ReadSealedAsync(owner, staged.UploadId, async (stream, token) =>
            {
                retained = stream;
                await stream.CopyToAsync(Stream.Null, token);
                return 0;
            });
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await retained!.ReadExactlyAsync(new byte[1]));
            UploadStatus malformed = await StageAsync(Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nEND:VCARD\n"));
            await Assert.ThrowsAsync<DomainValidationException>(() => validator.ValidateAsync(owner, malformed.UploadId));
            Assert.Equal("sealed", (await uploads.GetAsync(owner, malformed.UploadId)).State); // Integrity is separate from semantic validation.
            _ = await uploads.CancelAsync(owner, staged.UploadId);
            await Assert.ThrowsAsync<UploadException>(() => validator.ValidateAsync(owner, staged.UploadId));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
