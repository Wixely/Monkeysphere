using System.ComponentModel;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteTemporalInput(string Value, string Precision, bool IsApproximate = false, string? ApproximationNote = null)
{
    public TemporalValueInput ToCore()
    {
        if (Value is null) throw new DomainValidationException("A temporal value cannot be null.");
        if (!Enum.TryParse(Precision, ignoreCase: true, out TemporalPrecision precision) || !Enum.IsDefined(precision) ||
            !string.Equals(precision.ToString(), Precision, StringComparison.OrdinalIgnoreCase))
            throw new DomainValidationException("Use a named temporal precision from field schema discovery.");
        return new(Value, precision, IsApproximate, ApproximationNote);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteLocationInput(string? DisplayContext = null, string? Latitude = null, string? Longitude = null,
    string? AccuracyMetres = null, string? ApproximationRadiusKilometres = null)
{
    public LocationValueInput ToCore() => new(DisplayContext, Latitude, Longitude, AccuracyMetres, ApproximationRadiusKilometres);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteFieldInput(Guid FieldDefinitionId, string? ScalarValue = null, IReadOnlyList<string>? Tags = null,
    RemoteTemporalInput? Temporal = null, RemoteLocationInput? Location = null)
{
    public FieldValueInput ToCore()
    {
        if (Tags?.Any(tag => tag is null) == true) throw new DomainValidationException("Tags cannot contain null values.");
        return new(FieldDefinitionId, ScalarValue, Tags, Temporal?.ToCore(), Location?.ToCore());
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteRecordPatchChange(string Operation, Guid? FieldDefinitionId = null, string? Value = null,
    IReadOnlyList<string>? Values = null, string? ScalarValue = null, IReadOnlyList<string>? Tags = null,
    RemoteTemporalInput? Temporal = null, RemoteLocationInput? Location = null)
{
    public RecordPatchChange ToCore()
    {
        if (Tags?.Any(tag => tag is null) == true) throw new DomainValidationException("Tags cannot contain null values.");
        return new(Operation, FieldDefinitionId, Value, Values, ScalarValue, Tags, Temporal?.ToCore(), Location?.ToCore());
    }
}

public sealed record RemoteWriteError(string Code, string Message, string CorrelationId);
public sealed record RemoteRecordValidation(bool IsValid, Guid RecordTypeId, string DisplayName, IReadOnlyList<string> Aliases, int FieldCount, string SchemaRevision);

public sealed partial class RemoteRecordWriter(RecordCommandService commands, RecordBatchService batches, RecordDeletionService deletions,
    RelationshipCommandService relationshipCommands, StructureCommandService structureCommands, PresetCommandService presetCommands, RemoteCommandIdentityProvider identities,
    ICurrentDomainScope currentDomain, IHttpContextAccessor accessor, IDomainCatalog domains, IDomainCommands domainCommands,
    IApplicationCommandAudit audit, TimeProvider timeProvider, ILogger<RemoteRecordWriter> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, string, string, string, Exception?> LogAuditFailure =
        LoggerMessage.Define<string, string, string>(LogLevel.Warning, new EventId(1, "CommandAuditFailure"),
            "Command audit could not be stored for action {Action}, outcome {Outcome}, correlation {CorrelationId}.");

    public Task<CallToolResult> CreateAsync(Guid domainId, Guid recordTypeId, string displayName, Guid idempotencyKey,
        IReadOnlyList<RemoteFieldInput> values, IReadOnlyList<string>? aliases, CancellationToken cancellationToken) => RunAsync(domainId, "records.create", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, recordTypeId, displayName, values, aliases });
        RecordCommandIdentity identity = identities.Create(domainId, "records.write", "records.create", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        ValidateValues(values);
        return await commands.CreateAsync(identity, recordTypeId, displayName, values.Select(value => value.ToCore()).ToArray(), aliases, cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> PatchAsync(Guid domainId, Guid id, string expectedRevision, Guid idempotencyKey,
        IReadOnlyList<RemoteRecordPatchChange> changes, CancellationToken cancellationToken) => RunAsync(domainId, "records.patch", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, id, expectedRevision, changes });
        RecordCommandIdentity identity = identities.Create(domainId, "records.write", "records.patch", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        if (changes is null || changes.Count is < 1 or > RecordCommandLimits.MaximumPatchChanges || changes.Any(change => change is null))
            throw new DomainValidationException("Supply 1-1000 non-null patch changes.");
        return await commands.PatchAsync(identity, id, expectedRevision, changes.Select(change => change.ToCore()).ToArray(), cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> ValidateAsync(Guid domainId, Guid recordTypeId, string displayName,
        IReadOnlyList<RemoteFieldInput> values, IReadOnlyList<string>? aliases, CancellationToken cancellationToken) => RunAsync(domainId, "records.validate", async () =>
    {
        _ = identities.Create(domainId, "records.write", "records.create", Guid.CreateVersion7(), new string('0', 64));
        using IDisposable domain = currentDomain.Use(domainId);
        ValidateValues(values);
        PreparedRecord prepared = await commands.PrepareCreateAsync(recordTypeId, displayName,
            values.Select(value => value.ToCore()).ToArray(), aliases, cancellationToken).ConfigureAwait(false);
        return new RemoteRecordValidation(true, recordTypeId, prepared.DisplayName, prepared.Aliases, prepared.Values.Count, prepared.SchemaRevision);
    });

    private static void ValidateValues(IReadOnlyList<RemoteFieldInput> values)
    {
        if (values is null || values.Count > RecordCommandLimits.MaximumFields || values.Any(value => value is null))
            throw new DomainValidationException("Supply at most 1000 non-null field inputs.");
    }

    private async Task<CallToolResult> RunAsync<T>(Guid domainId, string actionName, Func<Task<T>> action)
    {
        try
        {
            T result = await action().ConfigureAwait(false);
            JsonElement json = JsonSerializer.SerializeToElement(result, JsonOptions);
            return new() { StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or DomainValidationException or
            ConcurrencyConflictException or CommandReplayException or RecordPreviewException or RecordCommandNotFoundException or DbException or IOException or OperationCanceledException)
        {
            string code = exception switch
            {
                UnauthorizedAccessException => "permission_denied",
                ConcurrencyConflictException => "stale_revision",
                CommandReplayException replay => replay.Code,
                RecordPreviewException preview => preview.Code,
                RecordCommandNotFoundException => "not_found",
                DbException or IOException or OperationCanceledException => "temporarily_unavailable",
                _ => "validation_failed",
            };
            string message = code == "temporarily_unavailable"
                ? "The command could not complete. Use the same retry key to determine its outcome."
                : exception.Message[..Math.Min(exception.Message.Length, 1024)];
            await RecordFailureAsync(domainId, actionName, code).ConfigureAwait(false);
            JsonElement json = JsonSerializer.SerializeToElement(new { error = new RemoteWriteError(code, message, accessor.HttpContext?.TraceIdentifier ?? "") }, JsonOptions);
            return new() { IsError = true, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
        }
    }

    private async Task RecordFailureAsync(Guid domainId, string action, string outcome)
    {
        if (domainId == Guid.Empty || (action != "domains.rename" && !domains.TryGet(domainId, out _))) return;
        string correlation = accessor.HttpContext?.TraceIdentifier ?? "";
        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(2));
            ApplicationCommandEvent entry = new(domainId, "mcp", action, outcome, correlation, timeProvider.GetUtcNow());
            if (action == "domains.rename")
                await domainCommands.RecordFailureAsync(entry, deadline.Token).ConfigureAwait(false);
            else
            {
                using IDisposable domain = currentDomain.Use(domainId);
                await audit.RecordAsync(entry, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is DbException or IOException or OperationCanceledException)
        {
            LogAuditFailure(logger, action, outcome, correlation, null);
        }
    }
}

[McpServerToolType]
[RemoteToolScopes("records.write")]
public sealed class MonkeysphereRecordWriteTools
{
    [McpServerTool(Name = "create_record", ReadOnly = false, Destructive = false)]
    [Description("Creates one record in an explicit domain. Requires records.write and a unique idempotencyKey; identical retries replay for 24 hours. Field inputs use schema-discovered shapes. Returns a committed receipt.")]
    public static Task<CallToolResult> CreateAsync(RemoteRecordWriter writer, Guid domainId, Guid recordTypeId, string displayName,
        Guid idempotencyKey, IReadOnlyList<RemoteFieldInput> values, IReadOnlyList<string>? aliases = null, CancellationToken cancellationToken = default) =>
        writer.CreateAsync(domainId, recordTypeId, displayName, idempotencyKey, values, aliases, cancellationToken);

    [McpServerTool(Name = "patch_record", ReadOnly = false, Destructive = true)]
    [Description("Patches one record using set_name, replace_aliases, set_field or clear_field. Unmentioned data is preserved. Requires records.write, explicit domainId, expectedRevision from get_record, and idempotencyKey. Identical retries replay for 24 hours.")]
    public static Task<CallToolResult> PatchAsync(RemoteRecordWriter writer, Guid domainId, Guid id, string expectedRevision,
        Guid idempotencyKey, IReadOnlyList<RemoteRecordPatchChange> changes, CancellationToken cancellationToken = default) =>
        writer.PatchAsync(domainId, id, expectedRevision, idempotencyKey, changes, cancellationToken);

    [McpServerTool(Name = "validate_record", ReadOnly = true, Destructive = false)]
    [Description("Validates proposed record creation through the same Core rules without saving. Requires records.write and an explicit domain. Returns normalized name/aliases, field count and schema revision.")]
    public static Task<CallToolResult> ValidateAsync(RemoteRecordWriter writer, Guid domainId, Guid recordTypeId, string displayName,
        IReadOnlyList<RemoteFieldInput> values, IReadOnlyList<string>? aliases = null, CancellationToken cancellationToken = default) =>
        writer.ValidateAsync(domainId, recordTypeId, displayName, values, aliases, cancellationToken);
}
