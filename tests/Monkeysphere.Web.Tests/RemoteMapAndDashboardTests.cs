using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// The map's one setting is the only thing in this application that makes a viewer's browser talk to a
/// third party, and the page puts a privacy notice in front of the operator before the checkbox. A
/// tool call carries no page, so these pin that the acknowledgement takes its place rather than the
/// switch being flippable in passing — and that a location recorded as approximate stays approximate
/// on the way out.
/// </summary>
public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task ExternalTilesCannotBeTurnedOnWithoutAcknowledgingWhatThatDiscloses()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(
            factory, ["records.read", "structure.write"]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        using JsonDocument initial = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_map_settings", new { domainId });
        RemoteMapSettings before = Structured(initial).Deserialize<RemoteMapSettings>(JsonOptions)!;

        // Off by default, and the disclosure is readable before the decision rather than only after a
        // refusal: a caller should be able to tell the operator what it is about to cost them.
        Assert.False(before.ExternalTilesEnabled);
        Assert.Equal("https://tile.openstreetmap.org", before.TileHost);
        Assert.Contains("IP address", before.Disclosure, StringComparison.Ordinal);
        Assert.Contains("infer the area being viewed", before.Disclosure, StringComparison.Ordinal);

        // Refused, and refused with the notice quoted, because a caller that has not acknowledged it
        // has by definition not been shown it.
        using JsonDocument unacknowledged = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "set_map_settings",
            new { domainId, externalTilesEnabled = true });
        AssertWriteError(unacknowledged, "validation_failed");
        Assert.Contains("IP address", Error(unacknowledged), StringComparison.Ordinal);

        // And nothing was turned on by the attempt.
        using JsonDocument stillOff = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_map_settings", new { domainId });
        Assert.False(Structured(stillOff).Deserialize<RemoteMapSettings>(JsonOptions)!.ExternalTilesEnabled);

        using JsonDocument enabled = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "set_map_settings",
            new { domainId, externalTilesEnabled = true, acknowledgeExternalRequests = true });
        Assert.True(Structured(enabled).Deserialize<RemoteMapSettings>(JsonOptions)!.ExternalTilesEnabled);

        // Turning a disclosure off discloses nothing, so no acknowledgement is asked for. Requiring
        // one would make the safer direction the harder one.
        using JsonDocument disabled = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "set_map_settings",
            new { domainId, externalTilesEnabled = false });
        Assert.False(Structured(disabled).Deserialize<RemoteMapSettings>(JsonOptions)!.ExternalTilesEnabled);

        // Acknowledging without enabling is not a way to leave consent lying around for later.
        using JsonDocument acknowledgedButOff = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "set_map_settings",
            new { domainId, externalTilesEnabled = false, acknowledgeExternalRequests = true });
        Assert.False(Structured(acknowledgedButOff).Deserialize<RemoteMapSettings>(JsonOptions)!.ExternalTilesEnabled);
        using JsonDocument afterwards = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "set_map_settings",
            new { domainId, externalTilesEnabled = true });
        AssertWriteError(afterwards, "validation_failed");
    }

    [Fact]
    public async Task McpReadsPinsAndKeepsAnApproximateLocationApproximate()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Located thing");
        FieldDefinition place = await records.CreateAndAttachFieldAsync(type.Id, new("Place", FieldTypes.Location, false));
        _ = await records.CreateRecordAsync(type.Id, "Greenwich", [new(place.Id,
            Location: new LocationValueInput("Royal Observatory", "51.4779", "-0.0015", "5", null))]);
        _ = await records.CreateRecordAsync(type.Id, "Vaguely Yorkshire", [new(place.Id,
            Location: new LocationValueInput("Somewhere in Yorkshire", "53.9600", "-1.0800", null, "40"))]);
        _ = await records.CreateRecordAsync(type.Id, "Sydney", [new(place.Id,
            Location: new LocationValueInput("Opera House", "-33.8568", "151.2153", null, null))]);

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        using JsonDocument allResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_map", new { domainId });
        RemotePage<RemoteMapPin> all = Structured(allResult).Deserialize<RemotePage<RemoteMapPin>>(JsonOptions)!;
        Assert.Equal(3, all.TotalCount);

        RemoteMapPin vague = all.Items.Single(pin => pin.RecordDisplayName == "Vaguely Yorkshire");

        // The radius survives. Without it a consumer would plot a forty-kilometre claim as a point and
        // report a person's home to within a street, which the operator never said.
        Assert.Equal(40, vague.ApproximationRadiusKilometres);
        Assert.Null(vague.AccuracyMetres);
        Assert.Equal("Somewhere in Yorkshire", vague.DisplayContext);

        RemoteMapPin greenwich = all.Items.Single(pin => pin.RecordDisplayName == "Greenwich");
        Assert.Equal(5, greenwich.AccuracyMetres);
        Assert.Null(greenwich.ApproximationRadiusKilometres);

        // A bounding box over Britain leaves Sydney out, which is the whole point of asking for one.
        using JsonDocument britain = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_map",
            new { domainId, south = 49.5, west = -8.5, north = 61.0, east = 2.0 });
        RemotePage<RemoteMapPin> narrowed = Structured(britain).Deserialize<RemotePage<RemoteMapPin>>(JsonOptions)!;
        Assert.Equal(2, narrowed.TotalCount);
        Assert.DoesNotContain("Sydney", narrowed.Items.Select(pin => pin.RecordDisplayName));

        object[] refused =
        [
            // Bounds that run the wrong way round are a mistake rather than an empty map.
            new { domainId, south = 60.0, north = 50.0 },
            new { domainId, south = -91.0 },
            new { domainId, east = 181.0 },
            new { domainId, page = 0 },
            new { domainId, pageSize = SpatialMapService.MaximumPageSize + 1 },
            new { domainId, fieldDefinitionIds = Enumerable.Range(0, SpatialMapService.MaximumLocationFields + 1).Select(_ => Guid.CreateVersion7()).ToArray() },
        ];
        foreach (object request in refused)
        {
            using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_map", request);
            AssertWriteError(response, "validation_failed");
        }

        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteMapLimits limits = Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!.MapLimits!;
        Assert.Equal(SpatialMapService.MaximumPage, limits.MaximumPage);
        Assert.Equal(SpatialMapService.MaximumPageSize, limits.MaximumPageSize);
        Assert.Equal(SpatialMapService.DefaultPageSize, limits.DefaultPageSize);
        Assert.Equal(SpatialMapService.MaximumLocationFields, limits.MaximumLocationFields);
    }

    [Fact]
    public async Task TheDashboardLooksForwardWhereTheCalendarLooksAtWhatWasStored()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (typeId, birthdayId) = await InstallPersonAsync(factory);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            // A birthday a few days from now, so it falls inside any sane look-ahead whenever the
            // suite runs, and recorded in a year long past so the projection is doing real work.
            DateOnly soon = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(3);
            _ = await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>().CreateRecordAsync(
                typeId, "Ada", [Day(birthdayId, new DateOnly(1990, soon.Month, soon.Day))]);
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(
            factory, ["records.read", "structure.write"]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        using JsonDocument settingsResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_dashboard_settings", new { domainId });
        RemoteDashboardSettings settings = Structured(settingsResult).Deserialize<RemoteDashboardSettings>(JsonOptions)!;

        // A deployment that never saved a configuration gets one derived from its own structure, and
        // the bounds come back with it so a caller can tell a chosen look-ahead from the default.
        Assert.Equal(DashboardService.DefaultUpcomingDays, settings.UpcomingDays);
        Assert.Equal(DashboardService.MaximumUpcomingDays, settings.MaximumUpcomingDays);
        Assert.Contains(birthdayId, settings.RecurringFieldDefinitionIds);

        using JsonDocument upcomingResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_upcoming_dates", new { domainId });
        RemoteUpcomingDate upcoming = Assert.Single(Structured(upcomingResult).Deserialize<RemoteUpcomingDate[]>(JsonOptions)!);

        // The next occurrence, not the stored one. This is exactly what separates the dashboard from
        // query_calendar and from a reminder's due date: comparing this with today gives a real
        // answer, where the stored 1990 date would always be in the past.
        Assert.Equal("Ada", upcoming.RecordDisplayName);
        Assert.True(upcoming.OccursAt > DateTimeOffset.UtcNow);
        Assert.StartsWith("1990-", upcoming.StoredValue, StringComparison.Ordinal);
        Assert.Equal("day", upcoming.Precision);
        Assert.False(upcoming.HasTime);

        // Narrowing the look-ahead past the date takes it out of the list, which is how the setting is
        // shown to be doing anything at all.
        using JsonDocument narrowed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "set_dashboard_settings",
            new { domainId, upcomingDays = 1 });
        RemoteDashboardSettings updated = Structured(narrowed).Deserialize<RemoteDashboardSettings>(JsonOptions)!;
        Assert.Equal(1, updated.UpcomingDays);

        // And changing the look-ahead did not clear the fields nobody mentioned, matching
        // set_graph_settings rather than the saved views' replace-everything semantics.
        Assert.Equal(settings.RecurringFieldDefinitionIds, updated.RecurringFieldDefinitionIds);
        Assert.Equal(settings.RecordTypeIds, updated.RecordTypeIds);

        using JsonDocument fewer = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_upcoming_dates", new { domainId });
        Assert.Empty(Structured(fewer).Deserialize<RemoteUpcomingDate[]>(JsonOptions)!);

        // Clearing the recurring fields explicitly is still possible: an empty list is a decision, an
        // omitted one is silence.
        using JsonDocument cleared = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "set_dashboard_settings",
            new { domainId, recurringFieldDefinitionIds = Array.Empty<Guid>() });
        Assert.Empty(Structured(cleared).Deserialize<RemoteDashboardSettings>(JsonOptions)!.RecurringFieldDefinitionIds);

        object[] refused =
        [
            new { domainId, upcomingDays = 0 },
            new { domainId, upcomingDays = DashboardService.MaximumUpcomingDays + 1 },
            new { domainId, recordTypeIds = new[] { Guid.CreateVersion7() } },
            new { domainId, recurringFieldDefinitionIds = new[] { Guid.CreateVersion7() } },
        ];
        foreach (object request in refused)
        {
            using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "set_dashboard_settings", request);
            AssertWriteError(response, "validation_failed");
        }
    }

    [Theory]
    [InlineData("records.read", true, false)]
    [InlineData("structure.write", true, true)]
    [InlineData("views.manage", false, false)]
    public async Task ReadingTheseSettingsIsSeparateFromChangingThem(string grant, bool canRead, bool canWrite)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities granted = Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.Equal(canRead, granted.Tools.Single(tool => tool.Name == "get_map_settings").Allowed);
        Assert.Equal(canWrite, granted.Tools.Single(tool => tool.Name == "set_map_settings").Allowed);
        Assert.Equal(canRead, granted.Tools.Single(tool => tool.Name == "get_dashboard_settings").Allowed);
        Assert.Equal(canWrite, granted.Tools.Single(tool => tool.Name == "set_dashboard_settings").Allowed);

        // The projections return record content, so they take the read grant rather than the one that
        // configures them: a credential that may reshape the dashboard is not thereby a reader.
        Assert.Equal(grant == "records.read", granted.Tools.Single(tool => tool.Name == "list_upcoming_dates").Allowed);
        Assert.Equal(grant == "records.read", granted.Tools.Single(tool => tool.Name == "query_map").Allowed);

        // Turning on an external request is the most consequential setting here, so the refusal is
        // asserted rather than inferred from the capability flag alone.
        using JsonDocument attempted = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "set_map_settings",
            new { domainId, externalTilesEnabled = true, acknowledgeExternalRequests = true });
        if (canWrite) _ = Structured(attempted); else AssertWriteError(attempted, "permission_denied");
    }

    [Fact]
    public async Task TheseSettingsAndPinsBelongToTheirOwnDomain()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        MonkeysphereDomain other = await factory.Services.GetRequiredService<IDomainRegistry>().CreateAsync("Other map domain");
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordType type = await records.CreateRecordTypeAsync("Domestic place");
            FieldDefinition place = await records.CreateAndAttachFieldAsync(type.Id, new("Place", FieldTypes.Location, false));
            _ = await records.CreateRecordAsync(type.Id, "Home", [new(place.Id,
                Location: new LocationValueInput(null, "51.5", "-0.1", null, null))]);
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(
            factory, ["records.read", "structure.write"]);

        using JsonDocument ourPins = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_map",
            new { domainId = MonkeysphereDomains.DefaultId });
        Assert.Equal(1, Structured(ourPins).Deserialize<RemotePage<RemoteMapPin>>(JsonOptions)!.TotalCount);

        using JsonDocument theirPins = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_map",
            new { domainId = other.Id });
        Assert.Equal(0, Structured(theirPins).Deserialize<RemotePage<RemoteMapPin>>(JsonOptions)!.TotalCount);

        // Enabling tiles in one domain does not enable them in another: the disclosure was made about
        // one deployment's map, and the content security policy names the host per response.
        using JsonDocument enabled = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "set_map_settings",
            new { domainId = other.Id, externalTilesEnabled = true, acknowledgeExternalRequests = true });
        Assert.True(Structured(enabled).Deserialize<RemoteMapSettings>(JsonOptions)!.ExternalTilesEnabled);
        using JsonDocument ours = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_map_settings",
            new { domainId = MonkeysphereDomains.DefaultId });
        Assert.False(Structured(ours).Deserialize<RemoteMapSettings>(JsonOptions)!.ExternalTilesEnabled);
    }

    /// <summary>The error message of a refused call, so a refusal can be asserted to carry its reason.</summary>
    private static string Error(JsonDocument response) => response.RootElement
        .GetProperty("result").GetProperty("structuredContent")
        .GetProperty("error").GetProperty("message").GetString() ?? string.Empty;
}
