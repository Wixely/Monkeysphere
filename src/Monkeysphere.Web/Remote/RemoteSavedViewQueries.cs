using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

/// <summary>
/// Running a saved view. This lives on the query facade rather than in the tool because the rows it
/// returns must be the same shape <c>query_records</c> and <c>get_record</c> return: a record
/// summary and its stored values, projected once. A second projection written beside it would agree
/// today and drift the first time a field type gains a way of being read.
/// </summary>
public sealed partial class MonkeysphereRemoteQueries
{
    public async Task<RemotePage<RemoteSavedViewRow>> RunSavedViewAsync(
        ISavedViewService views,
        IGalleryViewService gallery,
        Guid id,
        int page,
        int pageSize,
        bool includeValues,
        Guid? domainId,
        CancellationToken cancellationToken)
    {
        // The read grant, not the view grant. Running a view returns record content, so managing
        // views must not become a way to read records without holding records.read.
        DemandReadScope();
        DiscoveryPagination.Validate(page, pageSize);
        if (includeValues && pageSize > RemoteSavedViewProjection.MaximumRowsWithValues)
        {
            throw new DomainValidationException(
                $"A page of rows with values is at most {RemoteSavedViewProjection.MaximumRowsWithValues}, " +
                "because each row costs a record read. Pass includeValues false for a larger page.");
        }

        using IDisposable? domainScope = UseDomain(domainId);
        SavedViewDetails details = await views.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new RecordCommandNotFoundException("Saved view was not found in this domain.");

        PagedResult<RecordSummary> result = await service
            .SearchRecordsAsync(views.ToSearch(details, page, pageSize), cancellationToken).ConfigureAwait(false);

        if (!includeValues)
        {
            return new(
                [.. result.Items.Select(record => new RemoteSavedViewRow(MapSummary(record), [], []))],
                result.Page, result.PageSize, result.TotalCount);
        }

        // The columns the view asks for, plus the field it groups by. The browser's grid fetches the
        // same set for the same reason: a grouped view that could not read its own grouping field
        // would have nothing to group on.
        HashSet<Guid> wanted = [.. details.ColumnFieldDefinitionIds];
        if (details.View.GroupByFieldDefinitionId is Guid group)
        {
            wanted.Add(group);
        }

        if (details.View.Kind == SavedViewKind.Gallery)
        {
            // Built by the same projection the browser's gallery draws from, so a client rendering
            // this view sees the same collage, the same captions and the same connections rather
            // than a second arrangement of the same records that agrees only by coincidence.
            IReadOnlyList<GalleryPanel> panels = await gallery
                .BuildAsync(details, result.Items, cancellationToken).ConfigureAwait(false);
            return new(
                [.. panels.Select(panel => new RemoteSavedViewRow(
                    MapSummary(panel.Record),
                    // The same value projection every other remote read uses, so a gallery row's
                    // values are the shape a client already parses rather than a rendered line.
                    [.. panel.Details.SelectMany(detail => detail.Values).Select(MapValue)],
                    panel.Tags)
                {
                    Images = [.. panel.Images.Select(image =>
                        new RemoteGalleryImage(image.Id, image.Caption, image.IsCover))],
                    TotalImageCount = panel.TotalImageCount,
                    Related = [.. panel.Related.Select(relation => new RemoteGalleryRelation(
                        relation.RecordId, relation.DisplayName, relation.Label, relation.IsOutgoing,
                        relation.ImageId, relation.IsExpired))],
                    TotalRelatedCount = panel.TotalRelatedCount,
                })],
                result.Page, result.PageSize, result.TotalCount);
        }

        List<RemoteSavedViewRow> rows = [];
        foreach (RecordSummary record in result.Items)
        {
            RecordDetails? full = await service.GetRecordAsync(record.Id, cancellationToken).ConfigureAwait(false);

            // A record that vanished between the search and this read is reported with no values
            // rather than dropped, so the page still matches the count the search returned.
            rows.Add(new RemoteSavedViewRow(
                MapSummary(record),
                full is null ? [] : [.. full.Values.Where(value => wanted.Contains(value.FieldDefinitionId)).Select(MapValue)],
                // Carried only when the view says it shows them, so the flag means the same thing
                // here as it does in the grid rather than being advice a caller may ignore.
                details.View.ShowTags ? full?.Tags ?? [] : []));
        }

        return new(rows, result.Page, result.PageSize, result.TotalCount);
    }
}
