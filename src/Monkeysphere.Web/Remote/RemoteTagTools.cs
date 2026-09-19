using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

/// <summary>One catalogue tag as a remote caller sees it, appearance and membership included.</summary>
public sealed record RemoteTag(
    Guid Id,
    string Name,
    string Colour,
    string Icon,
    IReadOnlyList<Guid> DomainIds,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string Revision);

public sealed record RemoteTagUsage(Guid DomainId, int RecordCount);

internal static class RemoteTagProjection
{
    internal static RemoteTag Map(TagDefinition tag) => new(
        tag.Id, tag.Name, tag.Colour, tag.Icon, tag.DomainIds, tag.CreatedAtUtc, tag.UpdatedAtUtc, tag.Revision);
}

/// <summary>
/// Reading the tag catalogue. This is deployment-wide rather than domain-scoped, because a tag is:
/// the same label means the same tag everywhere. It discloses no record content, only the labels
/// themselves, and a hidden domain's membership is withheld without the backstage grant.
/// </summary>
[McpServerToolType]
[RemoteToolScopes("records.read", "tags.manage")]
public sealed class MonkeysphereTagReadTools
{
    [McpServerTool(Name = "list_tags", ReadOnly = true)]
    [Description("Lists the deployment's universal tags with their colour, icon and the domains that offer them. Requires records.read or tags.manage. Tags are deployment-wide: the same label is the same tag in every domain. Pass domainId to list only the tags offered in that domain; omit it for every tag. A hidden domain's membership is withheld unless the credential also holds backstage. Returns no record content.")]
    public static Task<CallToolResult> ListAsync(ITagCatalogue catalogue, IHttpContextAccessor accessor,
        Guid? domainId = null, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "records.read", "tags.manage");
            return
            (await catalogue.ListAsync(domainId, cancellationToken).ConfigureAwait(false))
                .Select(RemoteTagProjection.Map).ToArray();
        });

    [McpServerTool(Name = "count_tag_usage", ReadOnly = true)]
    [Description("Counts the records carrying one tag, per domain. Requires records.read or tags.manage and an explicit tagId. Call this before set_tag_domains or delete_tag: both remove the tag from the records of the domains they drop, which cannot be undone. Domains the credential cannot observe are omitted rather than reported as zero.")]
    public static Task<CallToolResult> CountUsageAsync(ITagCatalogue catalogue, IHttpContextAccessor accessor,
        Guid tagId, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "records.read", "tags.manage");
            return
            (await catalogue.CountUsageAsync(tagId, cancellationToken).ConfigureAwait(false))
                .Select(entry => new RemoteTagUsage(entry.Key, entry.Value))
                .OrderBy(usage => usage.DomainId).ToArray();
        });
}

/// <summary>
/// Changing the catalogue. This sits under its own grant rather than <c>structure.write</c> for the
/// same reason <c>domains.manage</c> does: it writes to the deployment registry rather than to one
/// domain, and two of its operations delete record content — in every domain that holds the tag,
/// including domains the caller never selected. <c>structure.write</c> authorizes no record
/// deletion today, and folding these in would widen it silently.
/// </summary>
[McpServerToolType]
[RemoteToolScopes("tags.manage")]
public sealed class MonkeysphereTagWriteTools
{
    [McpServerTool(Name = "create_tag", ReadOnly = false, Destructive = false)]
    [Description("Creates a tag in a domain, or adds an existing tag to it. Requires tags.manage, an explicit domainId and a name. Naturally idempotent and safe to retry: a name already in the catalogue resolves to that same tag and gains this domain rather than creating a second tag with the same label, so the returned id may be one that already existed. The stored spelling wins over the one supplied. A new tag gets a random legible colour and the # icon.")]
    public static Task<CallToolResult> CreateAsync(ITagCatalogue catalogue, IHttpContextAccessor accessor,
        Guid domainId, string name, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "tags.manage");
            return RemoteTagProjection.Map(
                await catalogue.EnsureAsync(name, domainId, cancellationToken).ConfigureAwait(false));
        });

    [McpServerTool(Name = "set_tag_appearance", ReadOnly = false, Destructive = false)]
    [Description("Sets a tag's colour and icon. Requires tags.manage, tagId, expectedRevision from list_tags, a colour as #rrggbb and an icon of one or two characters, where an emoji counts as one. Affects presentation only; no record changes.")]
    public static Task<CallToolResult> SetAppearanceAsync(ITagCatalogue catalogue, IHttpContextAccessor accessor,
        Guid tagId, string colour, string icon, string expectedRevision, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "tags.manage");
            return RemoteTagProjection.Map(
                await catalogue.SetAppearanceAsync(tagId, colour, icon, expectedRevision, cancellationToken).ConfigureAwait(false));
        });

    [McpServerTool(Name = "rename_tag", ReadOnly = false, Destructive = false)]
    [Description("Renames a tag everywhere it is used. Requires tags.manage, tagId, a new name and expectedRevision from list_tags. The catalogue changes at once while the records of each domain follow shortly after, because their text lives in separate domain databases and no transaction spans them: a domain may briefly still report the old label, and list_tags is authoritative meanwhile. Renaming onto a name another tag already holds is refused rather than merging the two.")]
    public static Task<CallToolResult> RenameAsync(ITagCatalogue catalogue, IHttpContextAccessor accessor,
        Guid tagId, string name, string expectedRevision, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "tags.manage");
            return RemoteTagProjection.Map(
                await catalogue.RenameAsync(tagId, name, expectedRevision, cancellationToken).ConfigureAwait(false));
        });

    [McpServerTool(Name = "set_tag_domains", ReadOnly = false, Destructive = true)]
    [Description("Replaces the domains that offer a tag. Requires tags.manage, tagId, the complete domainIds list and expectedRevision from list_tags. Destructive: a domain dropped from the list also loses the tag from every record in it, permanently. Call count_tag_usage first and send the full intended set, not a delta. Memberships in domains the credential cannot observe are left untouched rather than dropped.")]
    public static Task<CallToolResult> SetDomainsAsync(ITagCatalogue catalogue, IHttpContextAccessor accessor,
        Guid tagId, IReadOnlyList<Guid> domainIds, string expectedRevision, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "tags.manage");
            return RemoteTagProjection.Map(
                await catalogue.SetDomainsAsync(tagId, domainIds ?? [], expectedRevision, cancellationToken).ConfigureAwait(false));
        });

    [McpServerTool(Name = "delete_tag", ReadOnly = false, Destructive = true)]
    [Description("Deletes a tag from the deployment. Requires tags.manage, tagId and expectedRevision from list_tags. Destructive: the tag is removed from every record in every domain that holds it, permanently. Call count_tag_usage first. Records keep their other tags. Repeating a completed deletion reports not_found rather than replaying.")]
    public static Task<CallToolResult> DeleteAsync(ITagCatalogue catalogue, IHttpContextAccessor accessor,
        Guid tagId, string expectedRevision, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "tags.manage");
            await catalogue.DeleteAsync(tagId, expectedRevision, cancellationToken).ConfigureAwait(false);
            return new { tagId, outcome = "deleted" };
        });
}
