using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DnaX.RemoteAccess;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task ContactImportProtocolAppliesReplaysAndPagesDurableOutcomes()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (administration, credential, surface) = await EnableRelationshipWritesAsync(factory, ["contacts.import"]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        using IServiceScope scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        byte[] bytes = Encoding.UTF8.GetBytes("""
            BEGIN:VCARD
            VERSION:4.0
            FN:Imported remotely
            X-OPAQUE:round-trip
            END:VCARD
            BEGIN:VCARD
            VERSION:4.0
            FN:Skipped remotely
            END:VCARD
            """.Replace("\r\n", "\n", StringComparison.Ordinal));
        Guid uploadId = await StageMcpPreviewAsync(client, surface.EndpointPath!, credential.Secret, domainId, bytes);
        using JsonDocument created = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_contact_import",
            new { domainId, uploadId, idempotencyKey = Guid.NewGuid() });
        RemoteContactPreviewStatus preview = Structured(created).Deserialize<RemoteContactPreviewStatus>(JsonOptions)!;
        Guid importKey = Guid.NewGuid();
        var apply = new
        {
            domainId,
            previewId = preview.Preview.PreviewId,
            expectedRevision = preview.Preview.Revision,
            idempotencyKey = importKey,
            selections = new[]
            {
                new { contactIndex = 0, action = "create_separately", existingRecordId = (Guid?)null },
                new { contactIndex = 1, action = "skip", existingRecordId = (Guid?)null },
            },
        };
        using JsonDocument applied = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "apply_contact_import", apply);
        ContactImportReceipt receipt = Structured(applied).Deserialize<ContactImportReceipt>(JsonOptions)!;
        Assert.Equal(new(1, 0, 0, 1), receipt.Result);
        Assert.Equal(2, receipt.ContactCount);
        using JsonDocument resultOne = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_contact_import_result",
            new { domainId, idempotencyKey = importKey, page = 1, pageSize = 1 });
        RemoteContactImportResultPage firstPage = Structured(resultOne).Deserialize<RemoteContactImportResultPage>(JsonOptions)!;
        RemoteContactImportOutcome first = Assert.Single(firstPage.Outcomes);
        Assert.Equal("create_separately", first.Action);
        Assert.NotNull(first.RecordId);
        Assert.Matches("^[0-9a-f]{32}$", first.Revision!);
        using JsonDocument resultTwo = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_contact_import_result",
            new { domainId, idempotencyKey = importKey, page = 2, pageSize = 1 });
        RemoteContactImportOutcome second = Assert.Single(Structured(resultTwo).Deserialize<RemoteContactImportResultPage>(JsonOptions)!.Outcomes);
        Assert.Equal("skip", second.Action);
        Assert.Null(second.RecordId);
        Assert.Null(second.Revision);
        using JsonDocument cancelled = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "cancel_upload", new { domainId, uploadId });
        Assert.Equal("cancelled", Structured(cancelled).GetProperty("state").GetString());
        using JsonDocument replayed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "apply_contact_import", apply);
        Assert.Equal(receipt, Structured(replayed).Deserialize<ContactImportReceipt>(JsonOptions));
        var changedSelections = new[]
        {
            new { contactIndex = 0, action = "skip", existingRecordId = (Guid?)null },
            new { contactIndex = 1, action = "skip", existingRecordId = (Guid?)null },
        };
        using JsonDocument conflict = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "apply_contact_import",
            new { domainId, previewId = preview.Preview.PreviewId, expectedRevision = preview.Preview.Revision, idempotencyKey = importKey, selections = changedSelections });
        AssertWriteError(conflict, "retry_conflict");
        using JsonDocument consumed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "apply_contact_import",
            new { domainId, previewId = preview.Preview.PreviewId, expectedRevision = preview.Preview.Revision, idempotencyKey = Guid.NewGuid(), selections = apply.selections });
        AssertWriteError(consumed, "preview_consumed");
        using JsonDocument badAction = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "apply_contact_import",
            new { domainId, previewId = Guid.NewGuid(), expectedRevision = preview.Preview.Revision, idempotencyKey = Guid.NewGuid(), selections = new[] { new { contactIndex = 0, action = "erase", existingRecordId = (Guid?)null } } });
        AssertWriteError(badAction, "validation_failed");
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>().SearchRecordsAsync(new())).TotalCount);
        DnaXGeneratedCredential rotated = await administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, surface.Version, ["contacts.import"]);
        using JsonDocument foreign = await SendAsync(client, surface.EndpointPath!, rotated.Secret, "tools/call", "get_contact_import_result",
            new { domainId, idempotencyKey = importKey });
        AssertWriteError(foreign, "not_found");
    }

    [Fact]
    public async Task ContactPreviewAcceptsMaximumFileAndCancellationStopsEvidenceReads()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["contacts.import"]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        const string prefix = "BEGIN:VCARD\nVERSION:4.0\nFN:Maximum preview\nX-OPAQUE:";
        const string suffix = "\nEND:VCARD\n";
        byte[] bytes = Encoding.UTF8.GetBytes(prefix + new string('x', VCardParser.MaximumBytes - prefix.Length - suffix.Length) + suffix);
        Guid uploadId = await StageMcpPreviewAsync(client, surface.EndpointPath!, credential.Secret, domainId, bytes);
        var request = new { domainId, uploadId, idempotencyKey = Guid.NewGuid() };
        using JsonDocument noPerson = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_contact_import", request);
        AssertWriteError(noPerson, "validation_failed");
        using IServiceScope scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        using JsonDocument created = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_contact_import", request);
        RemoteContactPreviewStatus status = Structured(created).Deserialize<RemoteContactPreviewStatus>(JsonOptions)!;
        Guid previewId = status.Preview.PreviewId;
        Assert.Equal(1, status.Preview.ContactCount);
        using JsonDocument head = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "read_contact_import_evidence", new { domainId, previewId, contactIndex = 0, count = 1 });
        RemoteContactPreviewEvidence first = Structured(head).Deserialize<RemoteContactPreviewEvidence>(JsonOptions)!;
        Assert.True(first.TotalBytes > VCardParser.MaximumBytes);
        Assert.Equal((byte)'{', Assert.Single(Convert.FromBase64String(first.ContentBase64)));
        using JsonDocument tail = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "read_contact_import_evidence", new { domainId, previewId, contactIndex = 0, offset = first.TotalBytes - 128 });
        RemoteContactPreviewEvidence last = Structured(tail).Deserialize<RemoteContactPreviewEvidence>(JsonOptions)!;
        Assert.Null(last.NextOffset);
        Assert.Equal(128, Convert.FromBase64String(last.ContentBase64).Length);
        using JsonDocument cancelled = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "cancel_upload", new { domainId, uploadId });
        Assert.Equal("cancelled", Structured(cancelled).GetProperty("state").GetString());
        using JsonDocument denied = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "read_contact_import_evidence", new { domainId, previewId, contactIndex = 0 });
        AssertWriteError(denied, "preview_expired");
        using JsonDocument retry = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_contact_import", request);
        AssertWriteError(retry, "preview_expired");
        Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>().SearchRecordsAsync(new())).TotalCount);
    }

    [Theory]
    [InlineData("records.read")]
    [InlineData("records.write")]
    [InlineData("structure.write")]
    [InlineData("domains.manage")]
    public async Task ContactPreviewToolsRequireTheirPurposeGrant(string grant)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        using JsonDocument create = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_contact_import",
            new { domainId, uploadId = Guid.NewGuid(), idempotencyKey = Guid.NewGuid() });
        AssertWriteError(create, "permission_denied");
        using JsonDocument inspect = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_contact_import_preview",
            new { domainId, previewId = Guid.NewGuid() });
        AssertWriteError(inspect, "permission_denied");
        using JsonDocument evidence = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "read_contact_import_evidence",
            new { domainId, previewId = Guid.NewGuid(), contactIndex = 0 });
        AssertWriteError(evidence, "permission_denied");
        using JsonDocument apply = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "apply_contact_import",
            new { domainId, previewId = Guid.NewGuid(), expectedRevision = new string('0', 32), idempotencyKey = Guid.NewGuid(), selections = new[] { new { contactIndex = 0, action = "skip" } } });
        AssertWriteError(apply, "permission_denied");
        using JsonDocument result = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_contact_import_result",
            new { domainId, idempotencyKey = Guid.NewGuid() });
        AssertWriteError(result, "permission_denied");
    }

    [Fact]
    public async Task ContactPreviewProtocolPagesReconstructsEvidenceAndRejectsForeignOwners()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (administration, credential, surface) = await EnableRelationshipWritesAsync(factory, ["contacts.import"]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        using IServiceScope scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        string name = new('n', 190);
        string opaque = string.Concat(Enumerable.Repeat("Evidence\U0001F600", 10000));
        byte[] bytes = Encoding.UTF8.GetBytes($"BEGIN:VCARD\nVERSION:4.0\nFN:{name}\nEMAIL:evidence@example.test\nitem1.TEL:+441234567890\nitem1.X-ABLabel:Custom\nX-OPAQUE:{opaque}\nEND:VCARD\nBEGIN:VCARD\nVERSION:4.0\nFN:Second fixture\nEMAIL:evidence@example.test\nEND:VCARD\n");
        IVCardService vcards = scope.ServiceProvider.GetRequiredService<IVCardService>();
        VCardImportPreview browser = await vcards.PreviewAsync(bytes);
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        _ = await records.CreateRecordAsync(browser.RecordTypeId, name, []);
        browser = await vcards.PreviewAsync(bytes);
        Guid uploadId = await StageMcpPreviewAsync(client, surface.EndpointPath!, credential.Secret, domainId, bytes);
        var request = new { domainId, uploadId, idempotencyKey = Guid.NewGuid() };
        using JsonDocument created = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_contact_import", request);
        RemoteContactPreviewStatus status = Structured(created).Deserialize<RemoteContactPreviewStatus>(JsonOptions)!;
        Assert.True(status.IsCurrent);
        Assert.Equal(2, status.Preview.ContactCount);
        Guid previewId = status.Preview.PreviewId;
        using JsonDocument replay = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_contact_import", request);
        Assert.Equal(status, Structured(replay).Deserialize<RemoteContactPreviewStatus>(JsonOptions));
        using JsonDocument page = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_contact_import_preview", new { domainId, previewId, page = 1, pageSize = 1 });
        RemoteContactPreviewPage summary = Structured(page).Deserialize<RemoteContactPreviewPage>(JsonOptions)!;
        RemoteContactPreviewSummary first = Assert.Single(summary.Contacts);
        Assert.True(first.DisplayNameTruncated);
        Assert.Equal(160, first.DisplayName.Length);
        Assert.True(first.DuplicateCandidateCount > 0);
        Assert.Equal("merge_non_conflicting", first.RecommendedAction);
        using JsonDocument second = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_contact_import_preview", new { domainId, previewId, page = 2, pageSize = 1 });
        Assert.True(Assert.Single(Structured(second).Deserialize<RemoteContactPreviewPage>(JsonOptions)!.Contacts).InFileDuplicateCandidateCount > 0);
        using MemoryStream assembled = new();
        int offset = 0;
        int total = 0;
        while (true)
        {
            using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "read_contact_import_evidence", new { domainId, previewId, contactIndex = 0, offset });
            Assert.True(Encoding.UTF8.GetByteCount(response.RootElement.GetProperty("result").GetRawText()) <= new RemoteContactPreviewLimits().MaximumResponseBytes);
            RemoteContactPreviewEvidence evidence = Structured(response).Deserialize<RemoteContactPreviewEvidence>(JsonOptions)!;
            Assert.Equal("base64-utf8-json", evidence.Encoding);
            Assert.Equal(1, evidence.FormatVersion);
            Assert.Equal(offset, evidence.Offset);
            byte[] chunk = Convert.FromBase64String(evidence.ContentBase64);
            Assert.InRange(chunk.Length, 1, 16384);
            assembled.Write(chunk);
            total = evidence.TotalBytes;
            if (evidence.NextOffset is not int next) break;
            Assert.Equal(offset + chunk.Length, next);
            offset = next;
        }
        Assert.Equal(total, assembled.Length);
        using JsonDocument full = JsonDocument.Parse(assembled.ToArray());
        Assert.Equal(name, full.RootElement.GetProperty("displayName").GetString());
        Assert.Equal(browser.Contacts[0].Card.Fingerprint, full.RootElement.GetProperty("card").GetProperty("fingerprint").GetString());
        Assert.Equal(opaque, full.RootElement.GetProperty("card").GetProperty("properties").EnumerateArray().Single(property => property.GetProperty("name").GetString() == "X-OPAQUE").GetProperty("value").GetString());
        Assert.True(full.RootElement.GetProperty("duplicateCandidates").GetArrayLength() > 0);
        Assert.True(full.RootElement.GetProperty("fieldMappings").GetArrayLength() > 0);
        Assert.Equal(1, (await records.SearchRecordsAsync(new())).TotalCount);
        foreach (var invalid in new[] { new { contactIndex = 0, offset = -1, count = 1 }, new { contactIndex = 0, offset = 0, count = 16385 }, new { contactIndex = 0, offset = total + 1, count = 1 } })
        {
            using JsonDocument denied = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "read_contact_import_evidence", new { domainId, previewId, invalid.contactIndex, invalid.offset, invalid.count });
            AssertWriteError(denied, "validation_failed");
        }
        using JsonDocument missing = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "read_contact_import_evidence", new { domainId, previewId, contactIndex = 2 });
        AssertWriteError(missing, "not_found");
        using JsonDocument end = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "read_contact_import_evidence", new { domainId, previewId, contactIndex = 0, offset = total });
        Assert.Equal("", Structured(end).GetProperty("contentBase64").GetString());
        using JsonDocument badPage = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_contact_import_preview", new { domainId, previewId, pageSize = 26 });
        AssertWriteError(badPage, "validation_failed");
        _ = await records.CreateRecordAsync(browser.RecordTypeId, "Concurrent edit", []);
        using JsonDocument stale = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_contact_import", request);
        Assert.False(Structured(stale).GetProperty("isCurrent").GetBoolean());
        Assert.Equal(previewId, Structured(stale).GetProperty("preview").GetProperty("previewId").GetGuid());
        MonkeysphereDomain other = await factory.Services.GetRequiredService<IDomainCatalog>().CreateAsync("Evidence other domain");
        using JsonDocument foreign = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_contact_import_preview", new { domainId = other.Id, previewId });
        AssertWriteError(foreign, "not_found");
        DnaXGeneratedCredential rotated = await administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, surface.Version, ["contacts.import"]);
        using JsonDocument foreignCredential = await SendAsync(client, surface.EndpointPath!, rotated.Secret, "tools/call", "read_contact_import_evidence", new { domainId, previewId, contactIndex = 0 });
        AssertWriteError(foreignCredential, "not_found");
    }

    private static async Task<Guid> StageMcpPreviewAsync(HttpClient client, string endpoint, string secret, Guid domainId, byte[] bytes)
    {
        using JsonDocument begun = await SendAsync(client, endpoint, secret, "tools/call", "begin_upload", new
        {
            domainId,
            purpose = "contact_import",
            byteLength = bytes.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            contentType = "text/vcard",
            idempotencyKey = Guid.NewGuid()
        });
        Guid uploadId = Structured(begun).GetProperty("uploadId").GetGuid();
        for (int offset = 0; offset < bytes.Length; offset += RemoteUploadLimits.MaximumChunkBytes)
        {
            byte[] chunk = bytes[offset..Math.Min(offset + RemoteUploadLimits.MaximumChunkBytes, bytes.Length)];
            using JsonDocument written = await SendAsync(client, endpoint, secret, "tools/call", "write_upload_chunk", new
            {
                domainId,
                uploadId,
                offset,
                contentBase64 = Convert.ToBase64String(chunk),
                sha256 = Convert.ToHexString(SHA256.HashData(chunk))
            });
            Assert.Equal(offset + chunk.Length, Structured(written).GetProperty("acceptedBytes").GetInt32());
        }
        return uploadId;
    }
}
