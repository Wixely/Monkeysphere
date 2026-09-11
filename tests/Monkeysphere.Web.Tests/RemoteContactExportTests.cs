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
    public async Task ContactExportStreamsSelectedPeopleWithAStableDigest()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["contacts.export"]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        using IServiceScope scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        IVCardService vcards = scope.ServiceProvider.GetRequiredService<IVCardService>();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();

        // Import through the shared browser path so exported records carry mapped values and opaque provenance.
        byte[] source = Encoding.UTF8.GetBytes("BEGIN:VCARD\nVERSION:4.0\nFN:Exported first\nEMAIL:first@example.test\nNICKNAME:Firsty\nX-OPAQUE:round-trip\nEND:VCARD\nBEGIN:VCARD\nVERSION:4.0\nFN:Exported second\nEMAIL:second@example.test\nEND:VCARD\n");
        VCardImportPreview preview = await vcards.PreviewAsync(source);
        VCardImportResult imported = await vcards.ApplyAsync(preview,
            [new(0, VCardImportAction.CreateSeparately), new(1, VCardImportAction.CreateSeparately)]);
        Assert.Equal(2, imported.Created);
        Guid[] recordIds = (await records.SearchRecordsAsync(new())).Items.OrderBy(item => item.DisplayName, StringComparer.Ordinal)
            .Select(item => item.Id).ToArray();
        Assert.Equal(2, recordIds.Length);

        // A deliberately small count forces several ranges so the offset chain and digest stability are exercised.
        using MemoryStream assembled = new();
        string? digest = null;
        int offset = 0;
        int total = 0;
        while (true)
        {
            using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_contacts",
                new { domainId, recordIds, offset, count = 64 });
            Assert.True(Encoding.UTF8.GetByteCount(response.RootElement.GetProperty("result").GetRawText()) <= new RemoteContactExportLimits().MaximumResponseBytes);
            RemoteContactExport chunk = Structured(response).Deserialize<RemoteContactExport>(JsonOptions)!;
            Assert.Equal("base64-utf8-vcard", chunk.Encoding);
            Assert.Equal(1, chunk.FormatVersion);
            Assert.Equal(2, chunk.ContactCount);
            Assert.Equal(offset, chunk.Offset);
            Assert.Matches("^[0-9A-F]{64}$", chunk.ContentDigest);
            digest ??= chunk.ContentDigest;
            Assert.Equal(digest, chunk.ContentDigest);
            byte[] bytes = Convert.FromBase64String(chunk.ContentBase64);
            Assert.InRange(bytes.Length, 1, 64);
            assembled.Write(bytes);
            total = chunk.TotalBytes;
            if (chunk.NextOffset is not int next) break;
            Assert.Equal(offset + bytes.Length, next);
            offset = next;
        }

        byte[] document = assembled.ToArray();
        Assert.Equal(total, document.Length);
        Assert.Equal(digest, Convert.ToHexString(SHA256.HashData(document)));
        string text = Encoding.UTF8.GetString(document);
        Assert.Contains("FN:Exported first", text, StringComparison.Ordinal);
        Assert.Contains("FN:Exported second", text, StringComparison.Ordinal);
        Assert.Contains("X-OPAQUE:round-trip", text, StringComparison.Ordinal);
        Assert.Contains("EMAIL", text, StringComparison.Ordinal);
        // The exported document must be re-importable by the same parser.
        Assert.Equal(2, (await vcards.PreviewAsync(document)).Contacts.Count);

        // One request can also carry the complete document when it fits the response bound.
        using JsonDocument whole = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_contacts",
            new { domainId, recordIds });
        RemoteContactExport single = Structured(whole).Deserialize<RemoteContactExport>(JsonOptions)!;
        Assert.Null(single.NextOffset);
        Assert.Equal(document, Convert.FromBase64String(single.ContentBase64));

        // Selecting the same contacts in a different order is a different document, so the digest must change.
        using JsonDocument reversed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_contacts",
            new { domainId, recordIds = recordIds.Reverse().ToArray() });
        Assert.NotEqual(digest, Structured(reversed).Deserialize<RemoteContactExport>(JsonOptions)!.ContentDigest);

        // Editing an exported record changes the digest, which is how a client detects a torn read.
        RecordDetails first = (await records.GetRecordAsync(recordIds[0]))!;
        _ = await records.UpdateRecordAsync(first.Record.Id, "Exported first renamed", [], expectedRevision: first.Revision);
        using JsonDocument afterEdit = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_contacts",
            new { domainId, recordIds });
        Assert.NotEqual(digest, Structured(afterEdit).Deserialize<RemoteContactExport>(JsonOptions)!.ContentDigest);
    }

    [Fact]
    public async Task ContactExportRejectsUnselectableRecordsAndOutOfRangeReads()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["contacts.export"]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        using IServiceScope scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType person = (await records.ListRecordTypesAsync()).Single(type => type.PresetKey == "monkeysphere.person");
        RecordDetails contact = await records.CreateRecordAsync(person.Id, "Only contact", []);
        RecordType place = await records.CreateRecordTypeAsync("Place", null);
        RecordDetails notAContact = await records.CreateRecordAsync(place.Id, "Not a contact", []);
        Guid[] one = [contact.Record.Id];

        foreach (object arguments in new object[]
        {
            new { domainId, recordIds = Array.Empty<Guid>() },
            new { domainId, recordIds = new[] { contact.Record.Id, contact.Record.Id } },
            new { domainId, recordIds = new[] { Guid.NewGuid() } },
            new { domainId, recordIds = new[] { notAContact.Record.Id } },
            new { domainId, recordIds = new[] { contact.Record.Id, notAContact.Record.Id } },
            new { domainId, recordIds = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray() },
            new { domainId, recordIds = one, count = 0 },
            new { domainId, recordIds = one, count = 16385 },
            new { domainId, recordIds = one, offset = -1 },
        })
        {
            using JsonDocument invalid = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_contacts", arguments);
            AssertWriteError(invalid, "validation_failed");
        }

        using JsonDocument valid = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_contacts", new { domainId, recordIds = one });
        RemoteContactExport export = Structured(valid).Deserialize<RemoteContactExport>(JsonOptions)!;
        Assert.Null(export.NextOffset);
        // The last valid offset is totalBytes - 1; reading at or past the end must fail rather than return an empty range.
        using JsonDocument last = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_contacts",
            new { domainId, recordIds = one, offset = export.TotalBytes - 1 });
        Assert.Single(Convert.FromBase64String(Structured(last).Deserialize<RemoteContactExport>(JsonOptions)!.ContentBase64));
        // The end offset yields an empty final range, matching read_contact_import_evidence; beyond it fails.
        using JsonDocument end = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_contacts",
            new { domainId, recordIds = one, offset = export.TotalBytes });
        RemoteContactExport empty = Structured(end).Deserialize<RemoteContactExport>(JsonOptions)!;
        Assert.Equal("", empty.ContentBase64);
        Assert.Null(empty.NextOffset);
        using JsonDocument past = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_contacts",
            new { domainId, recordIds = one, offset = export.TotalBytes + 1 });
        AssertWriteError(past, "validation_failed");
    }

    [Fact]
    public async Task ContactExportCannotReachAnotherDomainOrAnUnknownOne()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType person = (await records.ListRecordTypesAsync()).Single(type => type.PresetKey == "monkeysphere.person");
        RecordDetails contact = await records.CreateRecordAsync(person.Id, "Default domain contact", []);
        MonkeysphereDomain other = await factory.Services.GetRequiredService<IDomainCatalog>().CreateAsync("Other export domain");
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["contacts.export"]);
        Guid[] recordIds = [contact.Record.Id];

        using JsonDocument mine = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_contacts",
            new { domainId = MonkeysphereDomains.DefaultId, recordIds });
        Assert.Equal(1, Structured(mine).Deserialize<RemoteContactExport>(JsonOptions)!.ContactCount);
        // The same record ID must not resolve through another domain's scope.
        using JsonDocument foreign = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_contacts",
            new { domainId = other.Id, recordIds });
        AssertWriteError(foreign, "validation_failed");
        using JsonDocument unknown = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_contacts",
            new { domainId = Guid.NewGuid(), recordIds });
        AssertWriteError(unknown, "validation_failed");
    }

    [Theory]
    [InlineData("contacts.import")]
    [InlineData("records.read")]
    [InlineData("records.write")]
    [InlineData("structure.write")]
    [InlineData("instance.read")]
    public async Task ContactExportRequiresItsOwnGrant(string grant)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);
        using JsonDocument denied = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_contacts",
            new { domainId = MonkeysphereDomains.DefaultId, recordIds = new[] { Guid.NewGuid() } });
        AssertWriteError(denied, "permission_denied");
        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities permissions = Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.False(permissions.Tools.Single(tool => tool.Name == "export_contacts").Allowed);
    }

    [Fact]
    public async Task ContactExportGrantIsSelectableAndDoesNotAuthorizeOtherSurfaces()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["contacts.export"]);
        Assert.Contains(RemoteCredentialManager.AvailableScopes(DnaXRemoteSurface.Mcp), option => option.Scope == "contacts.export");
        Assert.DoesNotContain(RemoteCredentialManager.AvailableScopes(DnaXRemoteSurface.Api), option => option.Scope == "contacts.export");

        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities permissions = Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.Equal("1.20", permissions.ContractVersion);
        Assert.True(permissions.Tools.Single(tool => tool.Name == "export_contacts").Allowed);
        Assert.Equal(new RemoteContactExportLimits(), permissions.ContactExportLimits);
        // Exporting must not imply importing, reading or writing anything else.
        foreach (string other in new[] { "preview_contact_import", "apply_contact_import", "begin_upload", "get_record", "create_record", "query_records", "delete_record" })
        {
            Assert.False(permissions.Tools.Single(tool => tool.Name == other).Allowed);
        }
        using JsonDocument denied = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_contact_import",
            new { domainId = MonkeysphereDomains.DefaultId, uploadId = Guid.NewGuid(), idempotencyKey = Guid.NewGuid() });
        AssertWriteError(denied, "permission_denied");
    }
}
