using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// A saved view was browser-only: the question an operator had already composed could not be reached
/// from anywhere else, so a remote caller wanting the same answer had to rebuild the filters, the
/// tags and the sort and hope it had rebuilt them identically. These pin the round trip, that running
/// a view agrees with the grid that inspired it, and the two separations that matter — managing views
/// is not reading records, and a view is not a schema change.
/// </summary>
public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task McpSavesReopensAndRunsAViewThatAgreesWithTheBrowsersGrid()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Viewed person");
        FieldDefinition city = await records.CreateAndAttachFieldAsync(type.Id, new("City", FieldTypes.Text, false));
        FieldDefinition score = await records.CreateAndAttachFieldAsync(type.Id, new("Score", FieldTypes.Number, false));
        RecordDetails ada = await records.CreateRecordAsync(
            type.Id, "Ada", [new(city.Id, "London"), new(score.Id, "10")], null, ["work", "london"]);
        _ = await records.CreateRecordAsync(type.Id, "Grace", [new(city.Id, "London"), new(score.Id, "4")], null, ["work"]);
        _ = await records.CreateRecordAsync(type.Id, "Someone else", [new(city.Id, "Leeds")], null, ["london"]);

        var (_, credential, surface) = await EnableRelationshipWritesAsync(
            factory, ["records.read", "views.manage"]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        // Deliberately untidy and deliberately repeating one tag in another case, because what comes
        // back is asserted to be what the browser's own normalization would have produced.
        string[] requestedTags = ["  Work  ", "WORK", "london"];
        using JsonDocument createdResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_saved_view", new
        {
            domainId,
            name = "  Working Londoners  ",
            recordTypeId = type.Id,
            columnFieldDefinitionIds = new[] { city.Id },
            filters = new[] { new RemoteRecordFilter(city.Id, "equals", "London") },
            tags = requestedTags,
            sortFieldDefinitionId = score.Id,
            sortDescending = true,
            showTags = true,
        });
        RemoteSavedView created = Structured(createdResult).Deserialize<RemoteSavedView>(JsonOptions)!;

        // Normalized by the service the browser uses, so a view saved remotely is a view the page
        // can open: the name trimmed, the tags de-duplicated case-insensitively.
        Assert.Equal("Working Londoners", created.Name);
        Assert.Equal(["Work", "london"], created.Tags);
        Assert.True(created.ShowTags);

        using JsonDocument listed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_saved_views", new { domainId });
        RemoteSavedView[] all = Structured(listed).Deserialize<RemoteSavedView[]>(JsonOptions)!;
        Assert.Equal(created.Id, Assert.Single(all).Id);

        // The list deliberately omits columns and filters, because answering a list with every
        // view's full detail is a read per view for something the caller has not asked for.
        Assert.Empty(all[0].ColumnFieldDefinitionIds);
        Assert.Empty(all[0].Filters);

        using JsonDocument fetched = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_saved_view",
            new { domainId, id = created.Id });
        RemoteSavedView full = Structured(fetched).Deserialize<RemoteSavedView>(JsonOptions)!;

        // Read back in the spelling a caller would have to send to save the same view again. An
        // operator name here rather than the enum's would make the value unusable as input.
        Assert.Equal(city.Id, Assert.Single(full.ColumnFieldDefinitionIds));
        Assert.Equal(new RemoteRecordFilter(city.Id, "equals", "London"), Assert.Single(full.Filters));
        Assert.Equal(score.Id, full.SortFieldDefinitionId);
        Assert.True(full.SortDescending);

        using JsonDocument ranResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "run_saved_view",
            new { domainId, id = created.Id });
        RemotePage<RemoteSavedViewRow> ran = Structured(ranResult).Deserialize<RemotePage<RemoteSavedViewRow>>(JsonOptions)!;

        // Every tag narrows, so only the record carrying both is in the view — the same meaning the
        // grid gives the same list, which is the agreement worth pinning.
        RemoteSavedViewRow row = Assert.Single(ran.Items);
        Assert.Equal(ada.Record.Id, row.Record.Id);
        Assert.Equal(1, ran.TotalCount);

        // The columns the view asks for, and only those: Score is sorted on but not shown.
        Assert.Equal("London", Assert.Single(row.Values).Value);
        Assert.Equal(city.Id, row.Values[0].FieldDefinitionId);
        Assert.Equal(["work", "london"], row.Tags);

        using JsonDocument withoutValues = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "run_saved_view",
            new { domainId, id = created.Id, includeValues = false, pageSize = 100 });
        RemoteSavedViewRow cheap = Assert.Single(Structured(withoutValues).Deserialize<RemotePage<RemoteSavedViewRow>>(JsonOptions)!.Items);
        Assert.Equal(ada.Record.Id, cheap.Record.Id);
        Assert.Empty(cheap.Values);

        using JsonDocument copyResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "duplicate_saved_view",
            new { domainId, id = created.Id, name = "Sailors" });
        RemoteSavedView copy = Structured(copyResult).Deserialize<RemoteSavedView>(JsonOptions)!;
        Assert.NotEqual(created.Id, copy.Id);
        Assert.Equal("Sailors", copy.Name);
        Assert.Equal(["Work", "london"], copy.Tags);
        Assert.Equal(city.Id, Assert.Single(copy.ColumnFieldDefinitionIds));

        using JsonDocument updatedResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "update_saved_view",
            new { domainId, id = created.Id, name = "Everyone", recordTypeId = type.Id });
        RemoteSavedView updated = Structured(updatedResult).Deserialize<RemoteSavedView>(JsonOptions)!;

        // A replacement rather than a merge. A caller clearing a view's filters and tags has no
        // other way to say so, so what it leaves out is stored empty.
        Assert.Equal("Everyone", updated.Name);
        Assert.Empty(updated.Filters);
        Assert.Empty(updated.Tags);
        Assert.Empty(updated.ColumnFieldDefinitionIds);
        Assert.False(updated.ShowTags);
        using JsonDocument everyone = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "run_saved_view",
            new { domainId, id = created.Id });
        Assert.Equal(3, Structured(everyone).Deserialize<RemotePage<RemoteSavedViewRow>>(JsonOptions)!.TotalCount);

        // The copy is untouched by the edit to its original.
        using JsonDocument copyAfter = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_saved_view",
            new { domainId, id = copy.Id });
        Assert.Equal(["Work", "london"], Structured(copyAfter).Deserialize<RemoteSavedView>(JsonOptions)!.Tags);

        using JsonDocument deleted = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_saved_view",
            new { domainId, id = created.Id });
        Assert.Equal(created.Id, Structured(deleted).Deserialize<RemoteSavedViewDeletion>(JsonOptions)!.Id);

        // Deleting what is already gone says so, so a caller can tell a completed delete from a
        // mistaken identifier rather than being told the same thing either way.
        using JsonDocument again = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_saved_view",
            new { domainId, id = created.Id });
        AssertWriteError(again, "not_found");

        // Nothing but the stored question was lost.
        Assert.Equal(3, (await records.SearchRecordsAsync(new RecordSearch(RecordTypeId: type.Id))).TotalCount);
    }

    [Theory]
    [InlineData("records.read", true, false, true)]
    [InlineData("views.manage", true, true, false)]
    [InlineData("structure.write", false, false, false)]
    public async Task McpSeparatesManagingSavedViewsFromReadingWhatTheyReturn(
        string grant, bool canRead, bool canWrite, bool canRun)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Guid typeId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            typeId = (await records.CreateRecordTypeAsync("Guarded type")).Id;
            ISavedViewService views = scope.ServiceProvider.GetRequiredService<ISavedViewService>();
            _ = await views.CreateAsync(new("Existing", typeId, null, [], []));
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities granted = Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.Equal(canRead, granted.Tools.Single(tool => tool.Name == "get_saved_view").Allowed);
        Assert.Equal(canWrite, granted.Tools.Single(tool => tool.Name == "create_saved_view").Allowed);
        Assert.Equal(canRun, granted.Tools.Single(tool => tool.Name == "run_saved_view").Allowed);

        using JsonDocument listed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_saved_views", new { domainId });
        if (canRead) _ = Structured(listed); else AssertWriteError(listed, "permission_denied");

        using JsonDocument written = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_saved_view",
            new { domainId, name = "Attempted", recordTypeId = typeId });
        if (canWrite) _ = Structured(written); else AssertWriteError(written, "permission_denied");

        // The separation this theory exists for: a credential that may compose and store questions is
        // not thereby a credential that may read the answers. Running a view returns record content,
        // so views.manage alone is refused here even though it may write the view. A grant that
        // cannot list gets an invented id, which it will never get far enough to mind.
        Guid existing = Guid.CreateVersion7();
        if (canRead)
        {
            existing = Structured(listed).Deserialize<RemoteSavedView[]>(JsonOptions)!.First(view => view.Name == "Existing").Id;
        }

        using JsonDocument run = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "run_saved_view",
            new { domainId, id = existing });
        if (canRun) _ = Structured(run); else AssertWriteError(run, "permission_denied");
    }

    [Fact]
    public async Task McpRefusesViewsThatBreachTheirBoundsOrReachOutsideTheirRecordType()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Bounded type");
        FieldDefinition mine = await records.CreateAndAttachFieldAsync(type.Id, new("Mine", FieldTypes.Text, false));
        RecordType other = await records.CreateRecordTypeAsync("Other type");
        FieldDefinition theirs = await records.CreateAndAttachFieldAsync(other.Id, new("Theirs", FieldTypes.Text, false));
        Guid viewId = (await scope.ServiceProvider.GetRequiredService<ISavedViewService>()
            .CreateAsync(new("Runnable", type.Id, null, [mine.Id], []))).View.Id;

        var (_, credential, surface) = await EnableRelationshipWritesAsync(
            factory, ["records.read", "views.manage"]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        object[] refused =
        [
            // A field of another record type: refused rather than silently dropped, because a view
            // quietly missing the column somebody asked for is worse than one that failed to save.
            new { domainId, name = "Foreign column", recordTypeId = type.Id, columnFieldDefinitionIds = new[] { theirs.Id } },
            new { domainId, name = "Foreign filter", recordTypeId = type.Id, filters = new[] { new RemoteRecordFilter(theirs.Id, "equals", "x") } },
            new { domainId, name = "Foreign sort", recordTypeId = type.Id, sortFieldDefinitionId = theirs.Id },
            new { domainId, name = "Too many columns", recordTypeId = type.Id, columnFieldDefinitionIds = Enumerable.Range(0, SavedViewService.MaximumColumns + 1).Select(_ => Guid.CreateVersion7()).ToArray() },
            new { domainId, name = "Too many filters", recordTypeId = type.Id, filters = Enumerable.Repeat(new RemoteRecordFilter(mine.Id, "equals", "x"), SavedViewService.MaximumFilters + 1).ToArray() },
            new { domainId, name = "Too many tags", recordTypeId = type.Id, tags = Enumerable.Range(0, SavedViewService.MaximumTags + 1).Select(index => $"tag{index}").ToArray() },
            new { domainId, name = new string('n', SavedViewService.MaximumNameLength + 1), recordTypeId = type.Id },
            new { domainId, name = "Too long a question", recordTypeId = type.Id, query = new string('q', SavedViewService.MaximumQueryLength + 1) },
            new { domainId, name = "Unnamed operator", recordTypeId = type.Id, filters = new[] { new RemoteRecordFilter(mine.Id, "roughly", "x") } },
        ];
        foreach (object request in refused)
        {
            using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_saved_view", request);
            AssertWriteError(response, "validation_failed");
        }

        // Nothing was stored by any of them, which is the half a per-request assertion cannot show.
        using JsonDocument listed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_saved_views", new { domainId });
        Assert.Equal(viewId, Assert.Single(Structured(listed).Deserialize<RemoteSavedView[]>(JsonOptions)!).Id);

        // A page of rows carrying values costs a record read each, so it is bounded below the plain
        // record query's hundred. Refused with the way out named rather than quietly clamped.
        using JsonDocument tooWide = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "run_saved_view",
            new { domainId, id = viewId, pageSize = RemoteSavedViewProjection.MaximumRowsWithValues + 1 });
        AssertWriteError(tooWide, "validation_failed");
        using JsonDocument allowed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "run_saved_view",
            new { domainId, id = viewId, pageSize = DiscoveryPagination.MaximumPageSize, includeValues = false });
        _ = Structured(allowed);

        using JsonDocument beyond = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "run_saved_view",
            new { domainId, id = viewId, page = DiscoveryPagination.MaximumPage + 1 });
        AssertWriteError(beyond, "validation_failed");

        // A view that does not exist is not a view returning nothing.
        using JsonDocument missing = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "run_saved_view",
            new { domainId, id = Guid.CreateVersion7() });
        AssertWriteError(missing, "not_found");
        using JsonDocument missingUpdate = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "update_saved_view",
            new { domainId, id = Guid.CreateVersion7(), name = "Ghost", recordTypeId = type.Id });
        AssertWriteError(missingUpdate, "not_found");
    }

    [Fact]
    public async Task ASavedViewBelongsToItsDomainAndIsInvisibleFromAnother()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        MonkeysphereDomain other = await factory.Services.GetRequiredService<IDomainRegistry>().CreateAsync("Other view domain");
        Guid viewId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordType type = await records.CreateRecordTypeAsync("Domestic type");
            viewId = (await scope.ServiceProvider.GetRequiredService<ISavedViewService>()
                .CreateAsync(new("Local", type.Id, null, [], []))).View.Id;
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(
            factory, ["records.read", "views.manage"]);

        // Empty rather than an error, the shape get_record and get_record_type already use for a
        // record absent from the selected domain.
        using JsonDocument elsewhere = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_saved_view",
            new { domainId = other.Id, id = viewId });
        Assert.Empty(elsewhere.RootElement.GetProperty("result").GetProperty("content").EnumerateArray());

        using JsonDocument theirList = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_saved_views",
            new { domainId = other.Id });
        Assert.Empty(Structured(theirList).Deserialize<RemoteSavedView[]>(JsonOptions)!);

        // And a view of one domain cannot be deleted by naming another, which would otherwise be a
        // way to reach across the isolation the domain selector exists to enforce.
        using JsonDocument theirDelete = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_saved_view",
            new { domainId = other.Id, id = viewId });
        AssertWriteError(theirDelete, "not_found");

        using JsonDocument ours = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_saved_view",
            new { domainId = MonkeysphereDomains.DefaultId, id = viewId });
        Assert.Equal(viewId, Structured(ours).Deserialize<RemoteSavedView>(JsonOptions)!.Id);
    }

    [Fact]
    public async Task CapabilitiesReportTheBoundsAViewIsHeldToRatherThanLeavingThemToBeDiscoveredByRefusal()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);

        using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities capabilities = Structured(response).Deserialize<RemoteCapabilities>(JsonOptions)!;
        RemoteSavedViewLimits limits = capabilities.SavedViewLimits!;

        Assert.Equal(SavedViewService.MaximumColumns, limits.MaximumColumns);
        Assert.Equal(SavedViewService.MaximumFilters, limits.MaximumFilters);
        Assert.Equal(SavedViewService.MaximumTags, limits.MaximumTags);
        Assert.Equal(SavedViewService.MaximumNameLength, limits.MaximumNameLength);
        Assert.Equal(SavedViewService.MaximumQueryLength, limits.MaximumQueryLength);
        Assert.Equal(RemoteSavedViewProjection.MaximumRowsWithValues, limits.MaximumRowsPerPageWithValues);
    }
}
