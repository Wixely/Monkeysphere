using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// A record is shown the same way wherever it is named: its picture beside its name, falling back to
/// its type's symbol. That only works if every projection that names a record also carries them, so
/// these pin the ones the browser reads — search results, relationships, and the map.
/// </summary>
public sealed class RecordIdentityProjectionTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static async Task<Guid> WithImageAsync(TestApplication application, Guid recordId)
    {
        RecordImage image = await application.Services.GetRequiredService<IRecordImageService>()
            .AddAsync(recordId, new MemoryStream(Png), "fixture.png");
        return image.Id;
    }

    [Fact]
    public async Task ASearchResultCarriesTheCoverImageAndTheTypeSymbol()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Person", symbol: "🙂");
        RecordDetails pictured = await records.CreateRecordAsync(type.Id, "Has A Picture", []);
        RecordDetails plain = await records.CreateRecordAsync(type.Id, "Has No Picture", []);
        Guid imageId = await WithImageAsync(application, pictured.Record.Id);

        IReadOnlyList<RecordSummary> found = (await records.SearchRecordsAsync(new RecordSearch(null, type.Id))).Items;

        RecordSummary withImage = found.Single(item => item.Id == pictured.Record.Id);
        Assert.Equal(imageId, withImage.ImageId);
        Assert.Equal("🙂", withImage.RecordTypeSymbol);

        RecordSummary withoutImage = found.Single(item => item.Id == plain.Record.Id);
        Assert.Null(withoutImage.ImageId);
        Assert.Equal("🙂", withoutImage.RecordTypeSymbol);
    }

    // A relationship names the record at the far end, so it must carry that end's picture, not this one's.
    [Fact]
    public async Task ARelationshipCarriesTheFarEndsPictureFromBothDirections()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType people = await records.CreateRecordTypeAsync("Person", symbol: "🙂");
        RecordType pets = await records.CreateRecordTypeAsync("Cat", symbol: "🐈");
        RecordDetails owner = await records.CreateRecordAsync(people.Id, "An Owner", []);
        RecordDetails cat = await records.CreateRecordAsync(pets.Id, "A Cat", []);
        Guid ownerImage = await WithImageAsync(application, owner.Record.Id);
        Guid catImage = await WithImageAsync(application, cat.Record.Id);

        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        RelationshipType keeps = await relationships.CreateTypeAsync(new("Keeps", RelationshipDirectionality.Directional, "Is kept by"));
        await relationships.CreateAsync(keeps.Id, owner.Record.Id, cat.Record.Id);

        RelationshipView fromOwner = Assert.Single(await relationships.ListForRecordAsync(owner.Record.Id));
        Assert.True(fromOwner.IsOutgoing);
        Assert.Equal(catImage, fromOwner.ImageId);
        Assert.Equal("🐈", fromOwner.RecordTypeSymbol);

        RelationshipView fromCat = Assert.Single(await relationships.ListForRecordAsync(cat.Record.Id));
        Assert.False(fromCat.IsOutgoing);
        Assert.Equal(ownerImage, fromCat.ImageId);
        Assert.Equal("🙂", fromCat.RecordTypeSymbol);
    }

    // The dashboard names a record beside every upcoming date, so its date source carries them too.
    [Fact]
    public async Task AnUpcomingDashboardDateCarriesTheRecordsPictureAndSymbol()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Person", symbol: "🙂");
        FieldDefinition birthday = await records.CreateAndAttachFieldAsync(type.Id, new("Birthday", FieldTypes.ExactDate, false));
        await records.SetFieldRecurrenceAsync(birthday.Id, FieldRecurrence.Annual);
        DateOnly soon = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Has A Birthday", [
            new FieldValueInput(birthday.Id, new DateOnly(1990, soon.Month, soon.Day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        ]);
        Guid imageId = await WithImageAsync(application, record.Record.Id);

        IDashboardService dashboard = application.Services.GetRequiredService<IDashboardService>();
        DashboardConfiguration configuration = new([type.Id], [birthday.Id]);
        DashboardUpcomingDate upcoming = Assert.Single(await dashboard.ListUpcomingAsync(configuration));

        Assert.Equal(imageId, upcoming.Source.ImageId);
        Assert.Equal("🙂", upcoming.Source.RecordTypeSymbol);
    }

    [Fact]
    public async Task AMapPinCarriesTheRecordsPictureAndSymbol()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Place", symbol: "📍");
        FieldDefinition where = await records.CreateAndAttachFieldAsync(type.Id, new("Where", FieldTypes.Location, false));
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Somewhere", [
            new FieldValueInput(where.Id, Location: new LocationValueInput("A place", "51.5", "-0.12")),
        ]);
        Guid imageId = await WithImageAsync(application, record.Record.Id);

        SpatialMapEntry pin = Assert.Single((await application.Services.GetRequiredService<ISpatialMapService>()
            .QueryAsync(new SpatialMapQuery())).Items);

        Assert.Equal(imageId, pin.ImageId);
        Assert.Equal("📍", pin.RecordTypeSymbol);
    }
}
