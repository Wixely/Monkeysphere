using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

/// <summary>
/// One pin. The approximation radius travels with the coordinate because a location recorded as
/// "somewhere within twenty kilometres" is not the same claim as a surveyed point, and a consumer
/// handed only latitude and longitude would treat the two identically.
/// </summary>
public sealed record RemoteMapPin(
    Guid FieldValueId,
    Guid RecordId,
    Guid RecordTypeId,
    string RecordTypeName,
    string RecordDisplayName,
    Guid FieldDefinitionId,
    string FieldName,
    string? DisplayContext,
    double Latitude,
    double Longitude,
    double? AccuracyMetres,
    double? ApproximationRadiusKilometres);

/// <summary>
/// Whether maps may reach an external basemap provider. Off by default and deliberately hard to turn
/// on by accident: enabling it makes the viewer's browser talk to a third party.
/// </summary>
public sealed record RemoteMapSettings(
    bool ExternalTilesEnabled,
    string TileHost,
    string Disclosure);

/// <summary>The bounds a map query is held to.</summary>
public sealed record RemoteMapLimits(
    int MaximumPage = SpatialMapService.MaximumPage,
    int MaximumPageSize = SpatialMapService.MaximumPageSize,
    int DefaultPageSize = SpatialMapService.DefaultPageSize,
    int MaximumLocationFields = SpatialMapService.MaximumLocationFields);

/// <summary>
/// Reading the map and its one setting. The query returns record content and takes
/// <c>records.read</c>; reading the setting takes either that or <c>structure.write</c>, the way the
/// graph settings do, because a caller about to change it has to be able to see it first.
/// </summary>
[McpServerToolType]
[RemoteToolScopes("records.read")]
public sealed class MonkeysphereMapTools
{
    /// <summary>
    /// The privacy cost of external tiles, worded as the settings page words it. A tool call carries
    /// no page, so the text has to travel with the setting: a caller that cannot see the notice cannot
    /// be said to have read it.
    /// </summary>
    internal const string Disclosure =
        "This is off by default. When enabled, a viewer's browser requests map tiles from " +
        "OpenStreetMap. The provider receives the viewer's IP address, browser metadata, the " +
        "Monkeysphere site origin, and can infer the area being viewed. Record names are not sent, " +
        "but tile requests describe the displayed area. The private coordinate grid remains " +
        "available while external tiles are disabled.";

    [McpServerTool(Name = "query_map", ReadOnly = true)]
    [Description("Lists the located records inside a bounding box as pins, each with its coordinate and how precise that coordinate claims to be. Requires records.read. Bounds default to the whole world; south must not exceed north, latitudes are -90 to 90 and longitudes -180 to 180. Optional recordTypeId, fieldDefinitionId and fieldDefinitionIds (at most 20) narrow it. Pages are 1-10000 and pageSize 1-500, defaulting to 100. approximationRadiusKilometres is the operator's own statement that a location is only good to within that distance: treat such a pin as an area, not a point. Omitted domainId selects Default. No tile is requested by this tool; it returns coordinates, and nothing here contacts an external provider.")]
    public static Task<CallToolResult> QueryAsync(
        ISpatialMapService map,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        double south = -90,
        double west = -180,
        double north = 90,
        double east = 180,
        Guid? recordTypeId = null,
        Guid? fieldDefinitionId = null,
        IReadOnlyList<Guid>? fieldDefinitionIds = null,
        int page = 1,
        int pageSize = SpatialMapService.DefaultPageSize,
        Guid? domainId = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "records.read");
            using IDisposable domain = currentDomain.Use(domainId ?? MonkeysphereDomains.DefaultId);

            // Core owns every bound, so none is restated here: one rule, in the place the browser is
            // held to it as well.
            PagedResult<SpatialMapEntry> result = await map.QueryAsync(new SpatialMapQuery(
                south, west, north, east, recordTypeId, fieldDefinitionId, page, pageSize,
                fieldDefinitionIds), cancellationToken).ConfigureAwait(false);
            return new RemotePage<RemoteMapPin>(
                [.. result.Items.Select(Map)], result.Page, result.PageSize, result.TotalCount);
        });

    [RemoteToolScopes("records.read", "structure.write")]
    [McpServerTool(Name = "get_map_settings", ReadOnly = true)]
    [Description("Gets whether maps may request an external basemap, the one host they would reach, and the privacy disclosure that applies to turning it on. Requires records.read or structure.write. Omitted domainId selects Default. The disclosure is returned whether or not tiles are enabled, so a caller can read it before deciding rather than only after being refused.")]
    public static Task<CallToolResult> GetSettingsAsync(
        IMapSettingsService settings,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid? domainId = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "records.read", "structure.write");
            using IDisposable domain = currentDomain.Use(domainId ?? MonkeysphereDomains.DefaultId);
            MapConfiguration configuration = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
            return new RemoteMapSettings(
                configuration.ExternalTilesEnabled, Security.SecurityHeaders.TileHost, Disclosure);
        });

    [RemoteToolScopes("structure.write")]
    [McpServerTool(Name = "set_map_settings", ReadOnly = false, Destructive = false)]
    [Description("Turns the external basemap on or off. Requires structure.write and an explicit domainId. Enabling it also requires acknowledgeExternalRequests true, and is refused with the disclosure quoted if that is missing: the settings page puts the privacy notice in front of the operator before the checkbox, and a tool call carries no page, so the acknowledgement is how the same thing is said here. Disabling needs no acknowledgement, because turning a disclosure off discloses nothing. Enabling changes what a viewer's browser contacts, not what any record holds, and the deployment's content security policy names the tile host only while this is on.")]
    public static Task<CallToolResult> SetSettingsAsync(
        IMapSettingsService settings,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid domainId,
        bool externalTilesEnabled,
        bool acknowledgeExternalRequests = false,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "structure.write");
            if (externalTilesEnabled && !acknowledgeExternalRequests)
            {
                throw new DomainValidationException(
                    "Enabling external map tiles requires acknowledgeExternalRequests true. " + Disclosure);
            }

            using IDisposable domain = currentDomain.Use(domainId);
            MapConfiguration saved = await settings
                .SaveAsync(new MapConfiguration(externalTilesEnabled), cancellationToken).ConfigureAwait(false);
            return new RemoteMapSettings(
                saved.ExternalTilesEnabled, Security.SecurityHeaders.TileHost, Disclosure);
        });

    private static RemoteMapPin Map(SpatialMapEntry entry) => new(
        entry.FieldValueId,
        entry.RecordId,
        entry.RecordTypeId,
        entry.RecordTypeName,
        entry.RecordDisplayName,
        entry.FieldDefinitionId,
        entry.FieldName,
        entry.DisplayContext,
        entry.Latitude,
        entry.Longitude,
        entry.AccuracyMetres,
        entry.ApproximationRadiusKilometres);
}
