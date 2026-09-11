using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// Retaining source material is only useful if it can be read back. These cover the two things a
/// person actually goes looking for later: a custom property the application understood nothing
/// about, and an embedded payload too large to have been shown anywhere.
/// </summary>
public sealed class RecordSourceTests
{
    private const string EmbeddedPhoto =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static byte[] Card(string fullName, string favourite, bool withPhoto = true) =>
        Encoding.UTF8.GetBytes($"""
            BEGIN:VCARD
            VERSION:3.0
            FN:{fullName}
            N:{fullName};;;;
            EMAIL;TYPE=home:{fullName.Replace(" ", ".", StringComparison.Ordinal)}@example.test
            X-MONKEYSPHERE-FAVOURITE:{favourite}
            {(withPhoto ? "PHOTO;ENCODING=b;TYPE=PNG:" + EmbeddedPhoto : "X-NOTE:none")}
            END:VCARD
            """);

    private static async Task<(Guid RecordId, Guid TypeId)> ImportAsync(IServiceProvider services, byte[] card)
    {
        IVCardService vcards = services.GetRequiredService<IVCardService>();
        VCardImportPreview preview = await vcards.PreviewAsync(card);
        VCardContactPreview contact = Assert.Single(preview.Contacts);
        await vcards.ApplyAsync(preview, [new(contact.Index, VCardImportAction.CreateSeparately)]);
        IMonkeysphereService records = services.GetRequiredService<IMonkeysphereService>();
        RecordSummary summary = Assert.Single(
            (await records.SearchRecordsAsync(new(contact.DisplayName, preview.RecordTypeId))).Items);
        return (summary.Id, preview.RecordTypeId);
    }

    [Fact]
    public async Task ImportedMaterialTheApplicationDidNotUnderstandStaysInspectable()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        await application.Services.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        (Guid recordId, _) = await ImportAsync(application.Services, Card("Ada Lovelace", "Analytical Engine"));

        IRecordSourceService sources = application.Services.GetRequiredService<IRecordSourceService>();
        RecordSourceSnapshot snapshot = Assert.IsType<RecordSourceSnapshot>(await sources.GetAsync(recordId));

        RecordSourceImport import = Assert.Single(snapshot.Imports);
        Assert.Equal(RecordSourceKinds.VCard, import.SourceKind);
        Assert.Equal("3.0", import.SourceFormat);
        Assert.False(string.IsNullOrWhiteSpace(import.Fingerprint));

        // The custom property is understood by nothing, so it exists only here.
        RecordSourceValue favourite = Assert.Single(snapshot.Values, value => value.Name == "X-MONKEYSPHERE-FAVOURITE");
        Assert.Equal(RecordSourceMapping.Opaque, favourite.Mapping);
        Assert.Equal("Analytical Engine", favourite.ValuePreview);
        Assert.False(favourite.IsPreviewTruncated);

        // A mapped property says where it landed, so a reader can tell retained from live data.
        RecordSourceValue email = Assert.Single(snapshot.Values, value => value.Name == "EMAIL");
        Assert.Equal(RecordSourceMapping.FieldValue, email.Mapping);
        Assert.NotNull(email.FieldDefinitionId);
        Assert.Equal("Email", email.FieldName);
        Assert.Contains(email.Parameters, parameter =>
            parameter.Name == "TYPE" && parameter.Values.Contains("home", StringComparer.Ordinal));

        // Every retained line names the import it arrived on.
        Assert.All(snapshot.Values, value => Assert.Equal(import.Id, value.ImportId));
    }

    [Fact]
    public async Task AnEmbeddedPayloadIsSummarizedInTheListingAndReadInBoundedRanges()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        await application.Services.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        (Guid recordId, _) = await ImportAsync(application.Services, Card("Grace Hopper", "COBOL"));

        IRecordSourceService sources = application.Services.GetRequiredService<IRecordSourceService>();
        RecordSourceSnapshot snapshot = Assert.IsType<RecordSourceSnapshot>(await sources.GetAsync(recordId));
        RecordSourceValue photo = Assert.Single(snapshot.Values, value => value.Name == "PHOTO");

        Assert.Equal(EmbeddedPhoto.Length, photo.ValueLength);
        Assert.Contains(photo.Parameters, parameter => parameter.Name == "ENCODING");
        // The listing must never carry the whole payload; that is what the ranged read is for.
        Assert.True(photo.ValuePreview.Length <= RecordSourceLimits.PreviewLength);

        RecordSourceValueRange first = await sources.ReadValueAsync(recordId, photo.Ordinal, 0, 40);
        Assert.Equal(EmbeddedPhoto.Length, first.TotalLength);
        Assert.Equal(EmbeddedPhoto[..40], first.Content);
        Assert.Equal(40, first.NextOffset);

        StringBuilder assembled = new(first.Content);
        int? next = first.NextOffset;
        while (next is int offset)
        {
            RecordSourceValueRange range = await sources.ReadValueAsync(recordId, photo.Ordinal, offset, 40);
            Assert.Equal(first.ContentDigest, range.ContentDigest);
            assembled.Append(range.Content);
            next = range.NextOffset;
        }

        Assert.Equal(EmbeddedPhoto, assembled.ToString());
        Assert.Equal(RecordSourceDigest.Of(EmbeddedPhoto), first.ContentDigest);

        // An offset at the end closes the chain; beyond it is a caller error, as elsewhere.
        RecordSourceValueRange end = await sources.ReadValueAsync(recordId, photo.Ordinal, EmbeddedPhoto.Length, 40);
        Assert.Equal(string.Empty, end.Content);
        Assert.Null(end.NextOffset);
        await Assert.ThrowsAsync<DomainValidationException>(
            () => sources.ReadValueAsync(recordId, photo.Ordinal, EmbeddedPhoto.Length + 1, 40));
        await Assert.ThrowsAsync<DomainValidationException>(
            () => sources.ReadValueAsync(recordId, photo.Ordinal, 0, RecordSourceLimits.MaximumRangeLength + 1));
    }

    [Fact]
    public async Task MergingASecondCardAttributesEachRetainedLineToTheImportItArrivedOn()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        await application.Services.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        (Guid recordId, _) = await ImportAsync(application.Services, Card("Ada Lovelace", "Analytical Engine"));

        IVCardService vcards = application.Services.GetRequiredService<IVCardService>();
        byte[] second = Card("Ada Lovelace", "Difference Engine", withPhoto: false);
        VCardImportPreview preview = await vcards.PreviewAsync(second);
        VCardContactPreview contact = Assert.Single(preview.Contacts);
        await vcards.ApplyAsync(preview, [new(contact.Index, VCardImportAction.MergeNonConflicting, recordId)]);

        IRecordSourceService sources = application.Services.GetRequiredService<IRecordSourceService>();
        RecordSourceSnapshot snapshot = Assert.IsType<RecordSourceSnapshot>(await sources.GetAsync(recordId));
        Assert.Equal(2, snapshot.Imports.Count);
        Assert.Equal(2, snapshot.Imports.Select(import => import.Fingerprint).Distinct(StringComparer.Ordinal).Count());

        RecordSourceValue original = Assert.Single(snapshot.Values, value => value.ValuePreview == "Analytical Engine");
        RecordSourceValue merged = Assert.Single(snapshot.Values, value => value.ValuePreview == "Difference Engine");
        Assert.NotNull(original.ImportId);
        Assert.NotNull(merged.ImportId);
        Assert.NotEqual(original.ImportId, merged.ImportId);

        // The embedded payload arrived on the first card only and must survive the merge.
        RecordSourceValue photo = Assert.Single(snapshot.Values, value => value.Name == "PHOTO");
        Assert.Equal(original.ImportId, photo.ImportId);
    }

    [Fact]
    public async Task ARecordWithNothingRetainedResolvesToNothing()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Hand made");
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Typed in by hand", []);

        IRecordSourceService sources = application.Services.GetRequiredService<IRecordSourceService>();
        Assert.Null(await sources.GetAsync(record.Record.Id));
        Assert.Null(await sources.GetAsync(Guid.CreateVersion7()));
    }
}
