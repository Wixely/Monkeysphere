using System.Security.Cryptography;
using System.Text.Json;
using DnaX.RemoteAccess;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

public sealed partial class RemoteDiscoveryTests
{
    // The same 1x1 PNG the existing image tests use: small enough for one chunk and known to
    // decode through the real SkiaSharp pipeline.
    private static byte[] SamplePng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static async Task<Guid> StageImageAsync(HttpClient client, string path, string secret, Guid domainId, byte[] bytes)
    {
        string digest = Convert.ToHexString(SHA256.HashData(bytes));
        using JsonDocument begun = await SendAsync(client, path, secret, "tools/call", "begin_upload", new
        {
            domainId,
            purpose = "record_image",
            byteLength = bytes.Length,
            sha256 = digest,
            contentType = "image/png",
            idempotencyKey = Guid.NewGuid(),
        });
        JsonElement status = Structured(begun);
        Assert.Equal("record_image", status.GetProperty("purpose").GetString());
        Guid uploadId = status.GetProperty("uploadId").GetGuid();
        using JsonDocument written = await SendAsync(client, path, secret, "tools/call", "write_upload_chunk", new
        {
            domainId,
            uploadId,
            offset = 0,
            contentBase64 = Convert.ToBase64String(bytes),
            sha256 = digest,
        });
        Assert.False(written.RootElement.GetProperty("result").TryGetProperty("isError", out JsonElement flag) && flag.GetBoolean());
        using JsonDocument completed = await SendAsync(client, path, secret, "tools/call", "complete_upload", new { domainId, uploadId });
        Assert.False(completed.RootElement.GetProperty("result").TryGetProperty("isError", out JsonElement done) && done.GetBoolean());
        return uploadId;
    }

    [Fact]
    public async Task RecordImageUploadAttachesAndReadsBackEveryVariant()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["media.write", "media.read", "records.read"]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Imaged");
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Has an image", []);

        byte[] png = SamplePng();
        Guid uploadId = await StageImageAsync(client, surface.EndpointPath!, credential.Secret, domainId, png);
        using JsonDocument added = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "add_record_image",
            new { domainId, recordId = record.Record.Id, uploadId, fileName = "portrait.png" });
        JsonElement image = Structured(added);
        Guid imageId = image.GetProperty("id").GetGuid();
        Assert.Equal("image/png", image.GetProperty("originalContentType").GetString());
        Assert.Equal(1, image.GetProperty("width").GetInt32());
        Assert.Equal("portrait.png", image.GetProperty("originalFileName").GetString());

        // The original must come back byte-identical; derivatives are regenerated WebP.
        foreach ((string variant, string expectedType) in new[] { ("original", "image/png"), ("preview", "image/webp"), ("thumbnail", "image/webp") })
        {
            byte[] assembled = [];
            string? digest = null;
            int offset = 0;
            while (true)
            {
                using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "read_record_image",
                    new { domainId, recordId = record.Record.Id, imageId, variant, offset, count = 64 });
                RemoteImageContent chunk = Structured(response).Deserialize<RemoteImageContent>(JsonOptions)!;
                Assert.Equal(expectedType, chunk.ContentType);
                digest ??= chunk.ContentDigest;
                Assert.Equal(digest, chunk.ContentDigest);
                assembled = [.. assembled, .. Convert.FromBase64String(chunk.ContentBase64)];
                if (chunk.NextOffset is not int next) break;
                offset = next;
            }

            Assert.Equal(digest, Convert.ToHexString(SHA256.HashData(assembled)));
            if (variant == "original") Assert.Equal(png, assembled);
        }

        // The staged upload is consumed; the record now owns exactly one image.
        RecordDetails reloaded = (await records.GetRecordAsync(record.Record.Id))!;
        Assert.Equal(imageId, Assert.Single(reloaded.Images).Id);
    }

    [Fact]
    public async Task RecordImageToolsRejectForeignPurposeAndUnselectableTargets()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory,
            ["media.write", "media.read", "records.read", "contacts.import"]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Imaged");
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Target", []);

        // Bytes staged as a contact file must never be attachable as an image.
        byte[] card = System.Text.Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nFN:Not an image\nEND:VCARD\n");
        Guid contactUpload = await StageMcpPreviewAsync(client, surface.EndpointPath!, credential.Secret, domainId, card);
        using JsonDocument mismatch = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "add_record_image",
            new { domainId, recordId = record.Record.Id, uploadId = contactUpload });
        AssertWriteError(mismatch, "purpose_mismatch");

        using JsonDocument unknownRecord = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "add_record_image",
            new { domainId, recordId = Guid.NewGuid(), uploadId = await StageImageAsync(client, surface.EndpointPath!, credential.Secret, domainId, SamplePng()) });
        AssertWriteError(unknownRecord, "validation_failed");

        foreach (object arguments in new object[]
        {
            new { domainId, recordId = record.Record.Id, imageId = Guid.NewGuid(), variant = "preview" },
            new { domainId, recordId = record.Record.Id, imageId = Guid.NewGuid(), variant = "not_a_variant" },
        })
        {
            using JsonDocument invalid = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "read_record_image", arguments);
            Assert.True(invalid.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        }
    }

    [Theory]
    [InlineData("records.read")]
    [InlineData("contacts.import")]
    [InlineData("contacts.export")]
    [InlineData("records.write")]
    public async Task RecordImageToolsRequireTheirOwnGrants(string grant)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        using JsonDocument add = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "add_record_image",
            new { domainId, recordId = Guid.NewGuid(), uploadId = Guid.NewGuid() });
        AssertWriteError(add, "permission_denied");
        using JsonDocument read = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "read_record_image",
            new { domainId, recordId = Guid.NewGuid(), imageId = Guid.NewGuid() });
        AssertWriteError(read, "permission_denied");

        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities permissions = Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.False(permissions.Tools.Single(tool => tool.Name == "add_record_image").Allowed);
        Assert.False(permissions.Tools.Single(tool => tool.Name == "read_record_image").Allowed);
    }
}
