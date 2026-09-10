using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using DnaX.Hosting;
using DnaX.RemoteAccess;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Data;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task UploadProtocolRejectsInvalidContentAndRevocationMidTransfer()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (administration, credential, surface) = await EnableRelationshipWritesAsync(factory, ["contacts.import"]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        byte[] bytes = Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nEND:VCARD\n"); // No formatted name.
        string digest = Convert.ToHexString(SHA256.HashData(bytes));
        var request = new { domainId, purpose = "contact_import", byteLength = bytes.Length, sha256 = digest, contentType = "text/vcard", idempotencyKey = Guid.NewGuid() };
        // record_image is a real purpose, but it needs media.write, which this credential does not
        // hold; importing contacts must never imply permission to stage image bytes.
        using JsonDocument wrongGrant = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "begin_upload", request with { purpose = "record_image" });
        AssertWriteError(wrongGrant, "permission_denied");
        using JsonDocument unsupported = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "begin_upload", request with { purpose = "not_a_purpose" });
        AssertWriteError(unsupported, "validation_failed");
        using JsonDocument begun = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "begin_upload", request);
        Guid uploadId = Structured(begun).GetProperty("uploadId").GetGuid();
        using JsonDocument conflict = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "begin_upload", request with { sha256 = new string('A', 64) });
        AssertWriteError(conflict, "retry_conflict");
        var write = new { domainId, uploadId, offset = 0, contentBase64 = Convert.ToBase64String(bytes), sha256 = digest };
        using JsonDocument invalidBase64 = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "write_upload_chunk", write with { contentBase64 = "not base64!" });
        AssertWriteError(invalidBase64, "validation_failed");
        using JsonDocument badDigest = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "write_upload_chunk", write with { sha256 = new string('A', 64) });
        AssertWriteError(badDigest, "validation_failed");
        using JsonDocument incomplete = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "complete_upload", new { domainId, uploadId });
        AssertWriteError(incomplete, "validation_failed");
        using JsonDocument written = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "write_upload_chunk", write);
        Assert.Equal(bytes.Length, Structured(written).GetProperty("acceptedBytes").GetInt64());
        using JsonDocument invalidContent = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "complete_upload", new { domainId, uploadId });
        AssertWriteError(invalidContent, "validation_failed");
        using JsonDocument pending = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "begin_upload", request with { idempotencyKey = Guid.NewGuid() });
        Guid pendingId = Structured(pending).GetProperty("uploadId").GetGuid();
        byte[] partial = bytes[..5];
        using JsonDocument started = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "write_upload_chunk",
            write with { uploadId = pendingId, contentBase64 = Convert.ToBase64String(partial), sha256 = Convert.ToHexString(SHA256.HashData(partial)) });
        Assert.Equal(5, Structured(started).GetProperty("acceptedBytes").GetInt64());
        _ = await administration.RevokeCredentialAsync(DnaXRemoteSurface.Mcp, surface.Version);
        using HttpRequestMessage revoked = CreateRequest(surface.EndpointPath!, credential.Secret, "tools/call", "get_upload_status", new { domainId, uploadId = pendingId });
        using HttpResponseMessage denied = await client.SendAsync(revoked);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, denied.StatusCode);
    }

    [Theory]
    [InlineData("records.read", false)]
    [InlineData("records.write", false)]
    [InlineData("structure.write", false)]
    [InlineData("domains.manage", false)]
    [InlineData("contacts.import", true)]
    public async Task UploadLifecycleRequiresItsPurposeGrant(string grant, bool allowed)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        byte[] bytes = Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nFN:Sample\nEND:VCARD\n");
        string digest = Convert.ToHexString(SHA256.HashData(bytes));
        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities discovered = Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.True(discovered.SupportsFileTransfer);
        Assert.Equal(allowed, discovered.Tools.Single(tool => tool.Name == "begin_upload").Allowed);
        Assert.Equal(256 * 1024, discovered.UploadLimits!.MaximumChunkBytes);
        using JsonDocument begun = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "begin_upload",
            new { domainId, purpose = "contact_import", byteLength = bytes.Length, sha256 = digest, contentType = "text/vcard", idempotencyKey = Guid.NewGuid() });
        Guid uploadId = allowed ? Structured(begun).Deserialize<UploadStatus>(JsonOptions)!.UploadId : Guid.NewGuid();
        if (!allowed) AssertWriteError(begun, "permission_denied");
        using JsonDocument status = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_upload_status", new { domainId, uploadId });
        using JsonDocument written = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "write_upload_chunk",
            new { domainId, uploadId, offset = 0, contentBase64 = Convert.ToBase64String(bytes), sha256 = digest });
        using JsonDocument complete = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "complete_upload", new { domainId, uploadId });
        using JsonDocument cancelled = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "cancel_upload", new { domainId, uploadId });
        if (allowed)
        {
            Assert.Equal("receiving", Structured(status).GetProperty("state").GetString());
            Assert.Equal(bytes.Length, Structured(written).GetProperty("acceptedBytes").GetInt64());
            Assert.Equal(1, Structured(complete).GetProperty("contactCount").GetInt32());
            Assert.True(Structured(complete).GetProperty("contentValidated").GetBoolean());
            Assert.Equal("cancelled", Structured(cancelled).GetProperty("state").GetString());
            using JsonDocument deniedRead = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_domains");
            Assert.True(deniedRead.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        }
        else
        {
            foreach (JsonDocument result in new[] { status, written, complete, cancelled }) AssertWriteError(result, "permission_denied");
        }
    }

    [Fact]
    public async Task FullContactFileUploadsReplaysExpiresAndKeepsAuditRedacted()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["contacts.import"]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        const string prefix = "BEGIN:VCARD\nVERSION:4.0\nFN:Private upload fixture\nX-OPAQUE:";
        const string suffix = "\nEND:VCARD\n";
        byte[] bytes = Encoding.UTF8.GetBytes(prefix + new string('x', RemoteUploadLimits.MaximumContactBytes - prefix.Length - suffix.Length) + suffix);
        var request = new { domainId, purpose = "contact_import", byteLength = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)), contentType = "text/vcard", idempotencyKey = Guid.NewGuid() };
        using JsonDocument begun = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "begin_upload", request);
        UploadStatus upload = Structured(begun).Deserialize<UploadStatus>(JsonOptions)!;
        for (int offset = 0; offset < bytes.Length; offset += upload.MaximumChunkBytes)
        {
            byte[] chunk = bytes[offset..Math.Min(offset + upload.MaximumChunkBytes, bytes.Length)];
            using JsonDocument written = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "write_upload_chunk",
                new { domainId, uploadId = upload.UploadId, offset, contentBase64 = Convert.ToBase64String(chunk), sha256 = Convert.ToHexString(SHA256.HashData(chunk)) });
            Assert.Equal(offset + chunk.Length, Structured(written).GetProperty("acceptedBytes").GetInt64());
        }
        using JsonDocument replayBegin = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "begin_upload", request);
        Assert.Equal(upload.UploadId, Structured(replayBegin).GetProperty("uploadId").GetGuid());
        Assert.Equal(bytes.Length, Structured(replayBegin).GetProperty("acceptedBytes").GetInt64());
        byte[] first = bytes[..upload.MaximumChunkBytes];
        using JsonDocument replayChunk = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "write_upload_chunk",
            new { domainId, uploadId = upload.UploadId, offset = 0, contentBase64 = Convert.ToBase64String(first), sha256 = Convert.ToHexString(SHA256.HashData(first)) });
        Assert.Equal(bytes.Length, Structured(replayChunk).GetProperty("acceptedBytes").GetInt64());
        using JsonDocument completed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "complete_upload", new { domainId, uploadId = upload.UploadId });
        Assert.Equal(1, Structured(completed).GetProperty("contactCount").GetInt32());
        using JsonDocument replayComplete = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "complete_upload", new { domainId, uploadId = upload.UploadId });
        Assert.Equal(Structured(completed).GetRawText(), Structured(replayComplete).GetRawText());
        MonkeysphereDomain other = await factory.Services.GetRequiredService<IDomainCatalog>().CreateAsync("Other file domain");
        using JsonDocument foreign = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_upload_status", new { domainId = other.Id, uploadId = upload.UploadId });
        AssertWriteError(foreign, "not_found");
        IDnaXPaths paths = factory.Services.GetRequiredService<IDnaXPaths>();
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = paths.ResolveWritable(RemoteUploadSchema.FileName) }.ConnectionString);
        await connection.OpenAsync();
        string audit = JsonSerializer.Serialize(await connection.QueryAsync("SELECT * FROM UploadAudit;"));
        Assert.DoesNotContain("Private upload fixture", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(credential.Secret, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(request.sha256, audit, StringComparison.Ordinal);
        Assert.Contains("validated", audit, StringComparison.Ordinal);
        await connection.ExecuteAsync("UPDATE UploadSessions SET ExpiresAtUtc = '2000-01-01T00:00:00.0000000+00:00';");
        using JsonDocument expired = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "complete_upload", new { domainId, uploadId = upload.UploadId });
        AssertWriteError(expired, "upload_expired");
        await factory.Services.GetRequiredService<IRemoteUploadStore>().CleanupAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM UploadChunks;"));
        DnaXGeneratedCredential rotated = await factory.Services.GetRequiredService<RemoteCredentialManager>().RotateAsync(DnaXRemoteSurface.Mcp, surface.Version, ["contacts.import"]);
        using JsonDocument notOwned = await SendAsync(client, surface.EndpointPath!, rotated.Secret, "tools/call", "get_upload_status", new { domainId, uploadId = upload.UploadId });
        AssertWriteError(notOwned, "not_found");
    }

    [Theory]
    [InlineData(4096, 0)]
    [InlineData(9216, 768)]
    [InlineData(65536, 43008)]
    public async Task UploadLimitsAccountForConfiguredRequestSize(long requestBytes, int expectedChunk)
    {
        await using SmallUploadFactory factory = new(requestBytes);
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["contacts.import"]);
        using JsonDocument result = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteUploadTransferLimits limits = Structured(result).Deserialize<RemoteCapabilities>(JsonOptions)!.UploadLimits!;
        Assert.Equal(expectedChunk, limits.MaximumChunkBytes);
        using JsonDocument denied = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "begin_upload",
            new { domainId = MonkeysphereDomains.DefaultId, purpose = "contact_import", byteLength = limits.MaximumContactBytes + 1, sha256 = new string('A', 64), contentType = "text/vcard", idempotencyKey = Guid.NewGuid() });
        AssertWriteError(denied, limits.MaximumContactBytes == RemoteUploadLimits.MaximumContactBytes ? "validation_failed" : "limit_exceeded");
    }

    private sealed class SmallUploadFactory(long maximumRequestBytes) : RemoteEnabledApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.PostConfigure<DnaXRemoteAccessOptions>(options => options.Limits.MaximumRequestBodyBytes = maximumRequestBytes));
        }
    }
}
