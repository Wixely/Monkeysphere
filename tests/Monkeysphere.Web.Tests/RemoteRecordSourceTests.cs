using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// Retained source material is reachable over MCP under the same grant that already exports it as a
/// vCard, because it is the same bytes either way. These pin that, the grant separation, and that a
/// backstage-hidden record discloses none of it.
/// </summary>
public sealed partial class RemoteDiscoveryTests
{
    private const string SourcePhoto =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static async Task<Guid> ImportContactAsync(RemoteEnabledApplicationFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        IVCardService vcards = scope.ServiceProvider.GetRequiredService<IVCardService>();
        byte[] card = Encoding.UTF8.GetBytes($"""
            BEGIN:VCARD
            VERSION:3.0
            FN:Ada Lovelace
            N:Lovelace;Ada;;;
            EMAIL;TYPE=home:ada@example.test
            X-MONKEYSPHERE-FAVOURITE:Analytical Engine
            PHOTO;ENCODING=b;TYPE=PNG:{SourcePhoto}
            END:VCARD
            """);
        VCardImportPreview preview = await vcards.PreviewAsync(card);
        VCardContactPreview contact = preview.Contacts[0];
        await vcards.ApplyAsync(preview, [new(contact.Index, VCardImportAction.CreateSeparately)]);
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        return (await records.SearchRecordsAsync(new("Ada Lovelace", preview.RecordTypeId))).Items[0].Id;
    }

    [Fact]
    public async Task RetainedSourceMaterialIsReadableUnderTheExportGrantAndNothingElse()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["contacts.export"]);
        Guid recordId = await ImportContactAsync(factory);
        Guid domainId = MonkeysphereDomains.DefaultId;

        using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "get_record_source", new { domainId, recordId });
        JsonElement source = Structured(response);
        Assert.Equal(recordId, source.GetProperty("recordId").GetGuid());

        JsonElement import = Assert.Single(source.GetProperty("imports").EnumerateArray().ToArray());
        Assert.Equal("vcard", import.GetProperty("sourceKind").GetString());
        Assert.Equal("3.0", import.GetProperty("sourceFormat").GetString());

        JsonElement[] values = source.GetProperty("values").EnumerateArray().ToArray();

        // The custom property exists only here, which is the reason for retaining any of this.
        JsonElement favourite = Assert.Single(values, value => value.GetProperty("name").GetString() == "X-MONKEYSPHERE-FAVOURITE");
        Assert.Equal("unused", favourite.GetProperty("usedAs").GetString());
        Assert.Equal("Analytical Engine", favourite.GetProperty("valuePreview").GetString());

        // A mapped line says where it landed, so a client can tell retained from live data.
        JsonElement email = Assert.Single(values, value => value.GetProperty("name").GetString() == "EMAIL");
        Assert.Equal("field_value", email.GetProperty("usedAs").GetString());
        Assert.Equal("Email", email.GetProperty("fieldName").GetString());

        // The embedded payload is summarized, never inlined.
        JsonElement photo = Assert.Single(values, value => value.GetProperty("name").GetString() == "PHOTO");
        Assert.Equal(SourcePhoto.Length, photo.GetProperty("valueLength").GetInt32());
        Assert.True(photo.GetProperty("valuePreview").GetString()!.Length <= RecordSourceLimits.PreviewLength);
        int ordinal = photo.GetProperty("ordinal").GetInt32();

        // …and is rebuilt through ranges whose digest must agree.
        StringBuilder assembled = new();
        string? digest = null;
        int? offset = 0;
        while (offset is int position)
        {
            using JsonDocument chunk = await SendAsync(client, surface.EndpointPath!, credential.Secret,
                "tools/call", "read_record_source_value", new { domainId, recordId, ordinal, offset = position, count = 40 });
            JsonElement range = Structured(chunk);
            digest ??= range.GetProperty("contentDigest").GetString();
            Assert.Equal(digest, range.GetProperty("contentDigest").GetString());
            assembled.Append(range.GetProperty("content").GetString());
            offset = range.TryGetProperty("nextOffset", out JsonElement next) && next.ValueKind == JsonValueKind.Number
                ? next.GetInt32()
                : null;
        }

        Assert.Equal(SourcePhoto, assembled.ToString());
        Assert.Equal(RecordSourceDigest.Of(SourcePhoto), digest);
    }

    [Fact]
    public async Task ReadingRetainedSourceMaterialRequiresTheExportGrant()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);
        Guid recordId = await ImportContactAsync(factory);

        foreach (string tool in new[] { "get_record_source", "read_record_source_value" })
        {
            using JsonDocument denied = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", tool,
                new { domainId = MonkeysphereDomains.DefaultId, recordId, ordinal = 0 });
            Assert.True(denied.RootElement.GetProperty("result").GetProperty("isError").GetBoolean(), tool);
        }
    }

    // Source material is record data, so hiding the record must hide its raw material too.
    [Fact]
    public async Task ABackstageHiddenRecordDisclosesNoRetainedSourceMaterial()
    {
        await using BackstageEnabledFactory factory = new(enabled: true);
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["contacts.export"]);
        Guid recordId = await ImportContactAsync(factory);
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBackstageRecordStore>()
                .SetStateAsync(recordId, BackstageStates.Hidden);
        }

        using JsonDocument hidden = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "get_record_source", new { domainId = MonkeysphereDomains.DefaultId, recordId });
        Assert.Empty(hidden.RootElement.GetProperty("result").GetProperty("content").EnumerateArray());

        using JsonDocument value = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "read_record_source_value",
            new { domainId = MonkeysphereDomains.DefaultId, recordId, ordinal = 0 });
        Assert.True(value.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }
}
