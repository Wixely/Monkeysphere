using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// The gallery view type. A saved view selects the same records whichever way it is drawn, so what is
/// worth testing is the projection: which pictures a collage draws and in what order, what the view's
/// chosen fields become when there is no table to head, and that a record's connections arrive as
/// something a panel can draw as a picture.
/// </summary>
public sealed class GalleryViewTests
{
    // A 1x1 PNG. What is in it does not matter; that a record has several of them does.
    private const string Pixel =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Fact]
    public async Task AGalleryLeadsWithTheCoverCountsTheRestAndCaptionsWithTheViewsOwnFields()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRecordImageService images = application.Services.GetRequiredService<IRecordImageService>();
        ISavedViewService views = application.Services.GetRequiredService<ISavedViewService>();
        IGalleryViewService gallery = application.Services.GetRequiredService<IGalleryViewService>();

        RecordType locomotive = await records.CreateRecordTypeAsync("Locomotive", "🚆");
        FieldDefinition number = await records.CreateAndAttachFieldAsync(locomotive.Id, new("Running number", FieldTypes.Text, false));
        FieldDefinition livery = await records.CreateAndAttachFieldAsync(locomotive.Id, new("Livery", FieldTypes.Text, false));
        FieldDefinition unused = await records.CreateAndAttachFieldAsync(locomotive.Id, new("Withdrawn", FieldTypes.Text, false));
        RecordDetails engine = await records.CreateRecordAsync(locomotive.Id, "Flying Scotsman",
            [new(number.Id, "60103"), new(livery.Id, "Apple green")], tags: ["preserved"]);

        // Seven pictures, so the collage has to choose. The sixth added is made the cover, which is
        // the one a person decided leads.
        List<Guid> added = [];
        for (int index = 0; index < 7; index++)
        {
            using MemoryStream content = new(Convert.FromBase64String(Pixel));
            RecordImage image = await images.AddAsync(engine.Record.Id, content, $"scotsman-{index}.png");
            added.Add(image.Id);
        }

        await images.UpdateMetadataAsync(engine.Record.Id, added[5], "At Doncaster", isCover: true);

        SavedViewDetails view = await views.CreateAsync(new SaveViewRequest(
            "Locomotives",
            locomotive.Id,
            Query: null,
            // Withdrawn is chosen but the record has nothing in it, which is the interesting case.
            ColumnFieldDefinitionIds: [number.Id, livery.Id, unused.Id],
            Filters: [],
            Tags: null,
            ShowTags: true,
            Kind: SavedViewKind.Gallery));

        GalleryPanel panel = Assert.Single(await gallery.BuildAsync(
            view, (await records.SearchRecordsAsync(views.ToSearch(view))).Items));

        // The collage is a sample, and it says so rather than pretending seven is five.
        Assert.Equal(SavedViewService.MaximumCollageImages, panel.Images.Count);
        Assert.Equal(7, panel.TotalImageCount);

        // The cover leads whatever order it was added in, because that is what making it the cover
        // meant, and the rest follow the record's own arrangement.
        Assert.Equal(added[5], panel.Images[0].Id);
        Assert.True(panel.Images[0].IsCover);
        Assert.Equal("At Doncaster", panel.Images[0].Caption);
        Assert.Equal([added[0], added[1], added[2], added[3]], panel.Images.Skip(1).Select(image => image.Id));

        // The view's columns become the caption. A grid shows an empty cell for a field the record
        // has nothing in because the column must line up; a caption has nothing to line up with, so
        // Withdrawn is absent rather than blank.
        Assert.Equal(["Running number", "Livery"], panel.Details.Select(detail => detail.FieldName));
        Assert.Equal("60103", panel.Details[0].Text);
        Assert.Equal("Apple green", panel.Details[1].Text);
        Assert.DoesNotContain(panel.Details, detail => detail.FieldDefinitionId == unused.Id);

        // Structured as well as rendered, so a remote client parses rather than re-reads.
        Assert.Equal("60103", Assert.Single(panel.Details[0].Values).TextValue);

        Assert.Equal(["preserved"], panel.Tags);
    }

    [Fact]
    public async Task APanelDrawsWhatTheRecordIsConnectedToWithThatRecordsOwnPicture()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRecordImageService images = application.Services.GetRequiredService<IRecordImageService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        ISavedViewService views = application.Services.GetRequiredService<ISavedViewService>();
        IGalleryViewService gallery = application.Services.GetRequiredService<IGalleryViewService>();

        RecordType locomotive = await records.CreateRecordTypeAsync("Locomotive", "🚆");
        RecordType depot = await records.CreateRecordTypeAsync("Depot", "🏭");
        RelationshipType allocatedTo = await relationships.CreateTypeAsync(
            new("allocated to", RelationshipDirectionality.Directional, "hosts"));

        RecordDetails engine = await records.CreateRecordAsync(locomotive.Id, "Flying Scotsman", []);
        RecordDetails photographed = await records.CreateRecordAsync(depot.Id, "Doncaster Works", []);
        RecordDetails unphotographed = await records.CreateRecordAsync(depot.Id, "Ambleside Shed", []);

        using MemoryStream content = new(Convert.FromBase64String(Pixel));
        RecordImage depotImage = await images.AddAsync(photographed.Record.Id, content, "doncaster.png");

        await relationships.CreateAsync(allocatedTo.Id, engine.Record.Id, unphotographed.Record.Id);
        await relationships.CreateAsync(allocatedTo.Id, engine.Record.Id, photographed.Record.Id);

        SavedViewDetails view = await views.CreateAsync(new SaveViewRequest(
            "Locomotives", locomotive.Id, null, [], [], Kind: SavedViewKind.Gallery));
        GalleryPanel panel = Assert.Single(await gallery.BuildAsync(
            view, (await records.SearchRecordsAsync(views.ToSearch(view))).Items));

        Assert.Equal(2, panel.TotalRelatedCount);

        // A connection with a picture is drawn before one without, because drawing these as pictures
        // is the entire reason a gallery carries them rather than listing names.
        GalleryRelation first = panel.Related[0];
        Assert.Equal(photographed.Record.Id, first.RecordId);
        Assert.Equal(depotImage.Id, first.ImageId);
        Assert.Equal("allocated to", first.Label);
        Assert.True(first.IsOutgoing);
        Assert.False(first.IsExpired);

        // And one without still appears, with its type's symbol to stand in for the picture.
        GalleryRelation second = panel.Related[1];
        Assert.Equal(unphotographed.Record.Id, second.RecordId);
        Assert.Null(second.ImageId);
        Assert.Equal("🏭", second.RecordTypeSymbol);
    }

    [Fact]
    public async Task AConnectionThatIsOverIsDrawnLastAndMarkedRatherThanDropped()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        ISavedViewService views = application.Services.GetRequiredService<ISavedViewService>();
        IGalleryViewService gallery = application.Services.GetRequiredService<IGalleryViewService>();

        RecordType locomotive = await records.CreateRecordTypeAsync("Locomotive", "🚆");
        RecordType depot = await records.CreateRecordTypeAsync("Depot", "🏭");
        RelationshipType allocatedTo = await relationships.CreateTypeAsync(
            new("allocated to", RelationshipDirectionality.Directional, "hosts"));

        RecordDetails engine = await records.CreateRecordAsync(locomotive.Id, "Flying Scotsman", []);
        RecordDetails current = await records.CreateRecordAsync(depot.Id, "Doncaster Works", []);
        RecordDetails former = await records.CreateRecordAsync(depot.Id, "Ambleside Shed", []);

        RelationshipView ended = await relationships.CreateAsync(allocatedTo.Id, engine.Record.Id, former.Record.Id);
        await relationships.CreateAsync(allocatedTo.Id, engine.Record.Id, current.Record.Id);
        await relationships.UpdateAsync(ended.Id, allocatedTo.Id, null, engine.Record.Id,
            expectedRevision: ended.Revision, expiry: new RelationshipExpiry(Expired: true));

        SavedViewDetails view = await views.CreateAsync(new SaveViewRequest(
            "Locomotives", locomotive.Id, null, [], [], Kind: SavedViewKind.Gallery));
        GalleryPanel panel = Assert.Single(await gallery.BuildAsync(
            view, (await records.SearchRecordsAsync(views.ToSearch(view))).Items));

        // Where a locomotive used to be allocated is a fact about the locomotive, not a mistake to
        // hide, so it is still drawn — after what is still true, and marked as over.
        Assert.Equal(2, panel.Related.Count);
        Assert.Equal(current.Record.Id, panel.Related[0].RecordId);
        Assert.False(panel.Related[0].IsExpired);
        Assert.Equal(former.Record.Id, panel.Related[1].RecordId);
        Assert.True(panel.Related[1].IsExpired);
    }

    [Fact]
    public async Task AGalleryCanGroupByAFieldItDoesNotAlsoPrint()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        ISavedViewService views = application.Services.GetRequiredService<ISavedViewService>();
        IGalleryViewService gallery = application.Services.GetRequiredService<IGalleryViewService>();

        RecordType locomotive = await records.CreateRecordTypeAsync("Locomotive", "🚆");
        FieldDefinition operatorField = await records.CreateAndAttachFieldAsync(locomotive.Id, new("Operator", FieldTypes.Text, false));
        FieldDefinition number = await records.CreateAndAttachFieldAsync(locomotive.Id, new("Running number", FieldTypes.Text, false));
        await records.CreateRecordAsync(locomotive.Id, "Flying Scotsman",
            [new(operatorField.Id, "LNER"), new(number.Id, "60103")]);

        // Grouped by Operator while printing only Running number. The grid fetches its grouping field
        // whether or not it is a column, and a gallery that did not would silently put every record
        // under one heading.
        SavedViewDetails view = await views.CreateAsync(new SaveViewRequest(
            "Locomotives", locomotive.Id, null,
            ColumnFieldDefinitionIds: [number.Id],
            Filters: [],
            GroupByFieldDefinitionId: operatorField.Id,
            Kind: SavedViewKind.Gallery));

        GalleryPanel panel = Assert.Single(await gallery.BuildAsync(
            view, (await records.SearchRecordsAsync(views.ToSearch(view))).Items));

        Assert.Equal("LNER", panel.GroupValue);
        Assert.Equal(["Running number"], panel.Details.Select(detail => detail.FieldName));
    }

    [Fact]
    public async Task TheKindIsStoredSurvivesDuplicationAndIsRefusedWhenItIsNotOneOfTheNamedOnes()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        ISavedViewService views = application.Services.GetRequiredService<ISavedViewService>();
        RecordType locomotive = await records.CreateRecordTypeAsync("Locomotive", "🚆");

        // A view saved before this change is a grid, and so is one that does not say.
        SavedViewDetails grid = await views.CreateAsync(new SaveViewRequest("Numbers", locomotive.Id, null, [], []));
        Assert.Equal(SavedViewKind.Grid, grid.View.Kind);

        SavedViewDetails saved = await views.CreateAsync(new SaveViewRequest(
            "Pictures", locomotive.Id, null, [], [], Kind: SavedViewKind.Gallery));
        Assert.Equal(SavedViewKind.Gallery, saved.View.Kind);
        Assert.Equal(SavedViewKind.Gallery,
            Assert.IsType<SavedViewDetails>(await views.GetAsync(saved.View.Id)).View.Kind);
        Assert.Equal(SavedViewKind.Gallery, Assert.Single(await views.ListAsync(), view => view.Id == saved.View.Id).Kind);

        // Copying a gallery gives a gallery. A copy that silently became a grid would be a surprising
        // way to lose the only thing that distinguishes the two.
        Assert.Equal(SavedViewKind.Gallery, (await views.DuplicateAsync(saved.View.Id, "Pictures copy")).View.Kind);

        // And the column is constrained, so a value outside the enum cannot be stored by any route.
        await Assert.ThrowsAsync<DomainValidationException>(() => views.CreateAsync(
            new SaveViewRequest("Invented", locomotive.Id, null, [], [], Kind: (SavedViewKind)7)));
    }
}
