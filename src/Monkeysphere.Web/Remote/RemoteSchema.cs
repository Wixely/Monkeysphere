using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

public sealed record RemoteTypeSummary(Guid Id, string Name, string? Symbol, string Lifecycle);

public sealed record RemoteReusableField(
    Guid Id, string Name, string TypeId, string ConfigurationJson, string Lifecycle,
    IReadOnlyList<string> ChoiceOptions, string? CanonicalKey, string? PresetKey, int? PresetVersion, string Revision = "");

public sealed record RemoteRelationshipType(Guid Id, string Name, string Directionality, string? InverseName, string Lifecycle, string Revision = "");

public sealed record RemoteFieldTypeSchema(
    string TypeId, bool Recognized, string ValueProperty, JsonElement ValueSchema,
    bool RequiresChoiceOptions, string Validation);

public sealed record RemoteFieldTypeCatalog(IReadOnlyList<RemoteFieldTypeSchema> Types, bool AllowsCustomTypes);

public sealed class MonkeysphereSchemaQueries(
    IMonkeysphereService records,
    IRelationshipService relationships,
    IDomainCatalog domains,
    ICurrentDomainScope currentDomain,
    IHttpContextAccessor accessor)
{
    public PagedResult<RemoteDomain> QueryDomains(int page, int pageSize)
    {
        RemoteReadAuthorization.Demand(accessor);
        DiscoveryPagination.Validate(page, pageSize);
        return DiscoveryPagination.From(domains.Snapshot.OrderBy(domain => domain.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(domain => domain.Id).Select(domain => new RemoteDomain(domain.Id, domain.Name, domain.IsDefault, domain.Revision)).ToArray(), page, pageSize);
    }

    public async Task<PagedResult<RemoteTypeSummary>> QueryTypesAsync(int page, int pageSize, Guid? domainId, CancellationToken cancellationToken)
    {
        RemoteReadAuthorization.Demand(accessor);
        DiscoveryPagination.Validate(page, pageSize);
        using IDisposable? domain = domainId is Guid id ? currentDomain.Use(id) : null;
        IReadOnlyList<RecordType> types = await records.ListRecordTypesAsync(cancellationToken).ConfigureAwait(false);
        return DiscoveryPagination.From(types.Select(type => new RemoteTypeSummary(type.Id, type.Name, type.Symbol,
            type.Lifecycle.ToString().ToLowerInvariant())).ToArray(), page, pageSize);
    }

    public async Task<PagedResult<RemoteReusableField>> QueryFieldsAsync(int page, int pageSize, Guid? domainId, CancellationToken cancellationToken)
    {
        RemoteReadAuthorization.Demand(accessor);
        DiscoveryPagination.Validate(page, pageSize);
        using IDisposable? domain = domainId is Guid id ? currentDomain.Use(id) : null;
        IReadOnlyList<FieldDefinition> fields = await records.ListFieldDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        return DiscoveryPagination.From(fields.Select(field => new RemoteReusableField(field.Id, field.Name, field.TypeId,
            field.ConfigurationJson, field.Lifecycle.ToString().ToLowerInvariant(), FieldTypes.ChoiceOptions(field),
            field.CanonicalKey, field.PresetKey, field.PresetVersion, field.Revision)).ToArray(), page, pageSize);
    }

    public async Task<PagedResult<RemoteRelationshipType>> QueryRelationshipTypesAsync(int page, int pageSize, Guid? domainId, CancellationToken cancellationToken)
    {
        RemoteReadAuthorization.Demand(accessor);
        DiscoveryPagination.Validate(page, pageSize);
        using IDisposable? domain = domainId is Guid id ? currentDomain.Use(id) : null;
        IReadOnlyList<RelationshipType> types = await relationships.ListTypesAsync(cancellationToken).ConfigureAwait(false);
        return DiscoveryPagination.From(types.Select(type => new RemoteRelationshipType(type.Id, type.Name,
            type.Directionality.ToString().ToLowerInvariant(), type.InverseName, type.Lifecycle.ToString().ToLowerInvariant(), type.Revision)).ToArray(), page, pageSize);
    }

    public async Task<PagedResult<RelationshipView>> QueryRelationshipsAsync(Guid recordId, int page, int pageSize, Guid? domainId, CancellationToken cancellationToken)
    {
        RemoteReadAuthorization.Demand(accessor);
        DiscoveryPagination.Validate(page, pageSize);
        using IDisposable? domain = domainId is Guid id ? currentDomain.Use(id) : null;
        return await relationships.QueryForRecordAsync(recordId, page, pageSize, cancellationToken).ConfigureAwait(false);
    }
}

internal static class RemoteReadAuthorization
{
    public static void Demand(IHttpContextAccessor accessor)
    {
        System.Security.Claims.ClaimsPrincipal? principal = accessor.HttpContext?.User;
        if (principal is null || !principal.Identities.Any(identity => identity.IsAuthenticated) ||
            !DnaX.RemoteAccess.DnaXRemoteAccessPrincipalExtensions.HasDnaXRemoteScope(principal, "records.read"))
        {
            throw new UnauthorizedAccessException("The records.read scope is required.");
        }
    }
}

[McpServerToolType]
[RemoteToolScopes("records.read")]
public sealed class MonkeysphereSchemaTools
{
    private static readonly JsonSerializerOptions SchemaOptions = CreateSchemaOptions();

    [McpServerTool(Name = "query_domains", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(PagedResult<RemoteDomain>))]
    [Description("Pages through isolated domains. Page size is 1-100. Requires records.read.")]
    public static CallToolResult QueryDomains(MonkeysphereSchemaQueries queries, IHttpContextAccessor accessor, int page = 1, int pageSize = 25) =>
        RemoteReadResults.Run(accessor, () => queries.QueryDomains(page, pageSize));

    [McpServerTool(Name = "query_record_types", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(PagedResult<RemoteTypeSummary>))]
    [Description("Pages through all record-type summaries, including retired types. Use get_record_type for fields. Requires records.read. An unknown domainId fails with a structured validation error.")]
    public static Task<CallToolResult> QueryTypesAsync(MonkeysphereSchemaQueries queries, IHttpContextAccessor accessor,
        int page = 1, int pageSize = 25, Guid? domainId = null, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => queries.QueryTypesAsync(page, pageSize, domainId, cancellationToken));

    [McpServerTool(Name = "list_field_definitions", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(PagedResult<RemoteReusableField>))]
    [Description("Pages through reusable field definitions, including configuration, choice options, lifecycle and provenance. Requires records.read. An unknown domainId fails with a structured validation error.")]
    public static Task<CallToolResult> QueryFieldsAsync(MonkeysphereSchemaQueries queries, IHttpContextAccessor accessor,
        int page = 1, int pageSize = 25, Guid? domainId = null, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => queries.QueryFieldsAsync(page, pageSize, domainId, cancellationToken));

    [McpServerTool(Name = "list_relationship_types", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(PagedResult<RemoteRelationshipType>))]
    [Description("Pages through relationship definitions with directionality, inverse labels and lifecycle. Requires records.read. An unknown domainId fails with a structured validation error.")]
    public static Task<CallToolResult> QueryRelationshipTypesAsync(MonkeysphereSchemaQueries queries, IHttpContextAccessor accessor,
        int page = 1, int pageSize = 25, Guid? domainId = null, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => queries.QueryRelationshipTypesAsync(page, pageSize, domainId, cancellationToken));

    [McpServerTool(Name = "query_record_relationships", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(PagedResult<RelationshipView>))]
    [Description("Pages through all relationships for one record, with total count and directional labels. Requires records.read. An unknown domainId fails with a structured validation error.")]
    public static Task<CallToolResult> QueryRelationshipsAsync(MonkeysphereSchemaQueries queries, IHttpContextAccessor accessor, Guid id,
        int page = 1, int pageSize = 25, Guid? domainId = null, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => queries.QueryRelationshipsAsync(id, page, pageSize, domainId, cancellationToken));

    [McpServerTool(Name = "get_field_type_schema", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(RemoteFieldTypeSchema))]
    [Description("Gets the Core input value shape for a field type. Unknown valid identifiers retain scalar text. Field-specific choices come from its definition. Requires records.read.")]
    public static CallToolResult GetFieldTypeSchema(IHttpContextAccessor accessor, string typeId) =>
        RemoteReadResults.Run(accessor, () =>
        {
            RemoteReadAuthorization.Demand(accessor);
            return BuildFieldTypeSchema(typeId);
        });

    private static RemoteFieldTypeSchema BuildFieldTypeSchema(string typeId)
    {
        string normalized = FieldTypes.NormalizeTypeId(typeId);
        (string property, Type type, string validation) = normalized switch
        {
            FieldTypes.Tags => ("tags", typeof(string[]), "At most 100 nonempty tags of at most 200 characters; case-insensitive duplicates are normalized."),
            FieldTypes.Temporal => ("temporal", typeof(TemporalValueInput), "Value must match the declared precision. Preserve approximation and its note."),
            FieldTypes.Location => ("location", typeof(LocationValueInput), "Coordinates must be paired. Accuracy requires coordinates. Context or coordinates are required for a radius."),
            FieldTypes.Number => ("scalarValue", typeof(string), "Invariant decimal text; validated by Core."),
            FieldTypes.ExactDate => ("scalarValue", typeof(string), "An exact date in YYYY-MM-DD format."),
            FieldTypes.Choice => ("scalarValue", typeof(string), "Must match an option from the field definition."),
            FieldTypes.PhoneNumber => ("scalarValue", typeof(string), "At most 200 characters; Core validates phone characters and minimum digit count."),
            FieldTypes.WebLink => ("scalarValue", typeof(string), "Absolute HTTP or HTTPS URL, at most 2048 characters."),
            _ => ("scalarValue", typeof(string), "At most 20000 characters; unknown types preserve the original nonempty text."),
        };
        JsonElement schema = JsonSerializer.SerializeToElement(SchemaOptions.GetJsonSchemaAsNode(type,
            new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true }));
        return new(normalized, FieldTypes.Recognized.Contains(normalized, StringComparer.Ordinal), property, schema,
            normalized == FieldTypes.Choice, validation);
    }

    [McpServerTool(Name = "list_field_types", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(RemoteFieldTypeCatalog))]
    [Description("Lists the built-in field types and their Core input shapes. Custom identifiers use the scalar-text fallback. Requires records.read.")]
    public static CallToolResult ListFieldTypes(IHttpContextAccessor accessor) =>
        RemoteReadResults.Run(accessor, () =>
        {
            RemoteReadAuthorization.Demand(accessor);
            return new RemoteFieldTypeCatalog(FieldTypes.Recognized.Select(BuildFieldTypeSchema).ToArray(), AllowsCustomTypes: true);
        });

    private static JsonSerializerOptions CreateSchemaOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.Converters.Add(new JsonStringEnumConverter<TemporalPrecision>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }
}
