namespace Monkeysphere.Core;

/// <summary>
/// One of a record's own images, as a gallery draws it.
///
/// Only what a collage needs: which image, what it is called, and whether the record considers it the
/// one to lead with. The bytes are fetched by the same media route every other picture in the
/// application uses, so nothing here carries them.
/// </summary>
public sealed record GalleryImage(Guid Id, string? Caption, bool IsCover);

/// <summary>
/// Something the record is connected to, drawn as a picture rather than named in a list.
///
/// What a photographed thing is connected to is usually another photographed thing: the locomotive
/// and the depot it lives at, the plant and the garden it grows in. So this carries the related
/// record's own cover image, and its type's symbol for when there is none.
/// </summary>
public sealed record GalleryRelation(
    Guid RecordId,
    string DisplayName,
    string Label,
    bool IsOutgoing,
    Guid? ImageId,
    string? RecordTypeSymbol)
{
    /// <summary>
    /// Whether this connection is over. Drawn faded rather than dropped, because a locomotive that
    /// used to be allocated somewhere is a fact about the locomotive, not a mistake to hide.
    /// </summary>
    public bool IsExpired { get; init; }
}

/// <summary>
/// One record as a gallery panel.
///
/// <see cref="TotalImageCount"/> is the record's whole count while <see cref="Images"/> holds only
/// what the collage draws, so a panel can honestly say there are more without being made forty
/// pictures tall by a record that has forty.
/// </summary>
public sealed record GalleryPanel(
    RecordSummary Record,
    IReadOnlyList<GalleryImage> Images,
    int TotalImageCount,
    IReadOnlyList<GalleryDetail> Details,
    IReadOnlyList<string> Tags,
    IReadOnlyList<GalleryRelation> Related,
    int TotalRelatedCount)
{
    /// <summary>
    /// What the view groups by, read whether or not it is also shown. Carried separately for the
    /// reason the grid fetches its grouping field separately: a view can group by something it does
    /// not print, and grouping off the printed details would silently put every record together.
    /// </summary>
    public string? GroupValue { get; init; }
}

/// <summary>
/// One of the view's chosen fields, as printed under the collage. Its columns, which in a grid would
/// be table headings: a gallery has no table, so they become the caption instead of being ignored.
///
/// Carries the stored values rather than only a rendered line, because a remote client wants them
/// structured — a date it can parse, a location it can put on a map — and re-reading the record to
/// get them back would be a second read for something this one already had.
/// </summary>
public sealed record GalleryDetail(Guid FieldDefinitionId, string FieldName, IReadOnlyList<RecordValue> Values)
{
    /// <summary>The values as one line, formatted the way the records grid formats the same field.</summary>
    public string Text => string.Join(", ", Values.Select(RecordValueText.Format));
}

/// <summary>Turns a record into what a gallery draws, so the browser and MCP draw from one projection.</summary>
public interface IGalleryViewService
{
    /// <summary>
    /// The panels for one page of a view's records.
    ///
    /// Takes the already-searched page rather than doing its own search, because the caller has
    /// usually applied ad-hoc narrowing on top of the view and the panels must describe the records
    /// actually on screen rather than a second, differently filtered set.
    /// </summary>
    Task<IReadOnlyList<GalleryPanel>> BuildAsync(
        SavedViewDetails view,
        IReadOnlyList<RecordSummary> summaries,
        CancellationToken cancellationToken = default);
}

public sealed class GalleryViewService(
    IMonkeysphereService records,
    IRelationshipService relationships,
    TimeProvider timeProvider) : IGalleryViewService
{
    public async Task<IReadOnlyList<GalleryPanel>> BuildAsync(
        SavedViewDetails view,
        IReadOnlyList<RecordSummary> summaries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(summaries);

        DateTimeOffset now = timeProvider.GetUtcNow();
        Dictionary<Guid, string> detailNames = await DetailFieldNamesAsync(view, cancellationToken).ConfigureAwait(false);
        Guid? groupFieldId = view.View.GroupByFieldDefinitionId;
        List<GalleryPanel> panels = [];

        foreach (RecordSummary summary in summaries)
        {
            RecordDetails? details = await records.GetRecordAsync(summary.Id, cancellationToken).ConfigureAwait(false);
            if (details is null)
            {
                // Gone between the search and this read. Drawn as an empty panel rather than dropped,
                // so the page still holds the number of records the count promised.
                panels.Add(new(summary, [], 0, [], [], [], 0));
                continue;
            }

            // The cover first and the rest in the order the record puts them, because a record's
            // image order is something somebody arranged and the collage should honour it.
            GalleryImage[] images =
            [
                .. details.Images
                    .OrderByDescending(image => image.IsCover)
                    .ThenBy(image => image.Ordinal)
                    .Take(SavedViewService.MaximumCollageImages)
                    .Select(image => new GalleryImage(image.Id, image.Caption, image.IsCover)),
            ];

            IReadOnlyList<RelationshipView> links = await relationships
                .ListForRecordAsync(summary.Id, SavedViewService.MaximumRelatedLinks + 1, cancellationToken)
                .ConfigureAwait(false);

            // A connection that is still true comes first, and one with a picture before one without,
            // because the point of drawing these is that they are pictures.
            GalleryRelation[] related =
            [
                .. links
                    .OrderBy(link => link.Expiry.IsExpiredAt(now))
                    .ThenByDescending(link => link.ImageId is not null)
                    .ThenBy(link => link.RelatedDisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .Take(SavedViewService.MaximumRelatedLinks)
                    .Select(link => new GalleryRelation(
                        link.RelatedRecordId, link.RelatedDisplayName, link.Label, link.IsOutgoing,
                        link.ImageId, link.RecordTypeSymbol)
                    {
                        IsExpired = link.Expiry.IsExpiredAt(now),
                    }),
            ];

            panels.Add(new(
                summary,
                images,
                details.Images.Count,
                Detail(details, detailNames),
                view.View.ShowTags ? details.Tags : [],
                related,
                links.Count)
            {
                GroupValue = groupFieldId is Guid field ? GroupText(details, field) : null,
            });
        }

        return panels;
    }

    /// <summary>
    /// The view's columns, in the order it lists them, named. A column whose field has since been
    /// detached from the type is left out rather than printed as an unnamed value.
    /// </summary>
    private async Task<Dictionary<Guid, string>> DetailFieldNamesAsync(
        SavedViewDetails view, CancellationToken cancellationToken)
    {
        if (view.ColumnFieldDefinitionIds.Count == 0) return [];
        RecordTypeDetails? type = await records
            .GetRecordTypeAsync(view.View.RecordTypeId, cancellationToken).ConfigureAwait(false);
        if (type is null) return [];
        Dictionary<Guid, string> attached = type.Fields
            .ToDictionary(field => field.Definition.Id, field => field.Definition.Name);
        return view.ColumnFieldDefinitionIds
            .Where(attached.ContainsKey)
            .ToDictionary(fieldId => fieldId, fieldId => attached[fieldId]);
    }

    private static string? GroupText(RecordDetails details, Guid fieldDefinitionId)
    {
        string text = string.Join(", ", details.Values
            .Where(value => value.FieldDefinitionId == fieldDefinitionId)
            .OrderBy(value => value.Ordinal)
            .Select(RecordValueText.Format));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static IReadOnlyList<GalleryDetail> Detail(
        RecordDetails details, Dictionary<Guid, string> names) =>
    [
        .. names
            .Select(name => new GalleryDetail(name.Key, name.Value,
                [.. details.Values
                    .Where(value => value.FieldDefinitionId == name.Key)
                    .OrderBy(value => value.Ordinal)]))
            // A field the record has nothing in is left out. A grid shows an empty cell because the
            // column has to line up; a caption has nothing to line up with, so a blank line is noise.
            .Where(detail => detail.Values.Count > 0),
    ];
}
