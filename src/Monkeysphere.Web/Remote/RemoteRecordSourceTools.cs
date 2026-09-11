using System.ComponentModel;
using System.Data.Common;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

public sealed record RemoteRecordSourceLimits(
    int MaximumValues = RecordSourceLimits.MaximumValues,
    int PreviewLength = RecordSourceLimits.PreviewLength,
    int MaximumChunkCharacters = RecordSourceLimits.MaximumRangeLength,
    int MaximumResponseBytes = 131072);

public sealed record RemoteRecordSourceImport(
    Guid Id, string SourceKind, string? SourceFormat, string? Fingerprint, DateTimeOffset ImportedAtUtc);

public sealed record RemoteRecordSourceValue(
    int Ordinal,
    Guid? ImportId,
    string? Grouping,
    string Name,
    IReadOnlyList<RecordSourceParameter> Parameters,
    string UsedAs,
    Guid? FieldDefinitionId,
    string? FieldName,
    int ValueLength,
    string ValuePreview,
    bool IsPreviewTruncated);

public sealed record RemoteRecordSource(
    Guid RecordId,
    IReadOnlyList<RemoteRecordSourceImport> Imports,
    IReadOnlyList<RemoteRecordSourceValue> Values);

public sealed record RemoteRecordSourceValueChunk(
    int Ordinal, string Name, int TotalLength, int Offset, int? NextOffset, string ContentDigest, string Content);

public sealed class RemoteRecordSourceCommands(
    IRecordSourceService sources,
    ICurrentDomainScope domains,
    RemoteCommandIdentityProvider identities,
    IHttpContextAccessor accessor)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly RemoteRecordSourceLimits Limits = new();

    public async Task<CallToolResult> GetAsync(Guid domainId, Guid recordId, CancellationToken cancellationToken)
    {
        try
        {
            // The same grant that already lets a credential read this material out as a vCard. It is
            // the same bytes either way, so a second permission would restrict nothing.
            _ = identities.CreateUploadOwner(domainId, "contacts.export");
            using IDisposable selection = domains.Use(domainId);
            RecordSourceSnapshot? snapshot = await sources.GetAsync(recordId, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                // A record that does not resolve, and one that retained nothing, are reported the
                // same way, so this cannot be used to probe for records.
                return new() { Content = [] };
            }

            RemoteRecordSource value = new(
                snapshot.RecordId,
                snapshot.Imports.Select(import => new RemoteRecordSourceImport(
                    import.Id, import.SourceKind, import.SourceFormat, import.Fingerprint, import.ImportedAtUtc)).ToArray(),
                snapshot.Values.Select(item => new RemoteRecordSourceValue(
                    item.Ordinal, item.ImportId, item.Grouping, item.Name, item.Parameters,
                    UsedAs(item.Mapping), item.FieldDefinitionId, item.FieldName,
                    item.ValueLength, item.ValuePreview, item.IsPreviewTruncated)).ToArray());
            return Respond(value);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Failure(exception);
        }
    }

    public async Task<CallToolResult> ReadValueAsync(
        Guid domainId, Guid recordId, int ordinal, int offset, int count, CancellationToken cancellationToken)
    {
        try
        {
            _ = identities.CreateUploadOwner(domainId, "contacts.export");
            using IDisposable selection = domains.Use(domainId);
            RecordSourceValueRange range = await sources
                .ReadValueAsync(recordId, ordinal, offset, count, cancellationToken).ConfigureAwait(false);
            return Respond(new RemoteRecordSourceValueChunk(
                range.Ordinal, range.Name, range.TotalLength, range.Offset,
                range.NextOffset, range.ContentDigest, range.Content));
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Failure(exception);
        }
    }

    private static string UsedAs(RecordSourceMapping mapping) => mapping switch
    {
        RecordSourceMapping.DisplayName => "display_name",
        RecordSourceMapping.Aliases => "aliases",
        RecordSourceMapping.FieldValue => "field_value",
        _ => "unused",
    };

    private static bool IsExpected(Exception exception) =>
        exception is UnauthorizedAccessException or DomainValidationException or DbException or IOException
            or OperationCanceledException;

    private static CallToolResult Respond<T>(T value)
    {
        JsonElement json = JsonSerializer.SerializeToElement(value, JsonOptions);
        CallToolResult result = new() { StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
        if (JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions).Length > Limits.MaximumResponseBytes)
        {
            throw new DomainValidationException("The response exceeds its byte limit. Request a smaller count.");
        }

        return result;
    }

    private CallToolResult Failure(Exception exception)
    {
        string code = exception switch
        {
            UnauthorizedAccessException => "permission_denied",
            DomainValidationException => "validation_failed",
            _ => "temporarily_unavailable",
        };
        string message = code == "temporarily_unavailable"
            ? "The request could not complete. Retry with the same identifiers."
            : exception.Message[..Math.Min(exception.Message.Length, 1024)];
        string correlation = accessor.HttpContext?.TraceIdentifier ?? string.Empty;
        correlation = correlation[..Math.Min(correlation.Length, 128)];
        JsonElement json = JsonSerializer.SerializeToElement(
            new { error = new RemoteWriteError(code, message, correlation) }, JsonOptions);
        return new() { IsError = true, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}

[McpServerToolType]
[RemoteToolScopes("contacts.export")]
public sealed class MonkeysphereRecordSourceTools
{
    [McpServerTool(Name = "get_record_source", ReadOnly = true)]
    [Description("Lists the raw material an importer retained for one record, exactly as it arrived. Requires contacts.export and an explicit domainId and recordId. Returns the import occasions (source kind, the producer's own format label, the content fingerprint of the imported item, and when it happened) and every retained line with its grouping, name, parameters, and what the application actually did with it: display_name, aliases, field_value, or unused. Lines marked unused were understood by nothing and exist nowhere else in the record, which is where custom properties and embedded payloads are found. Values are summarized, not returned: each line carries its total character length and a preview of at most 256 characters, with isPreviewTruncated set when there is more. Read a full value with read_record_source_value. A record that does not resolve and a record that retained nothing both return empty content. importId is null for material retained before imports were individually attributed.")]
    public static Task<CallToolResult> GetAsync(
        RemoteRecordSourceCommands commands, Guid domainId, Guid recordId, CancellationToken cancellationToken = default) =>
        commands.GetAsync(domainId, recordId, cancellationToken);

    [McpServerTool(Name = "read_record_source_value", ReadOnly = true)]
    [Description("Reads one retained source value in full through bounded character ranges. Requires contacts.export and an explicit domainId, recordId and ordinal from get_record_source. Offset starts at 0; count 1-16384. Concatenate content in offset order and follow nextOffset until null to rebuild the value; it is returned as text exactly as retained, so an embedded payload arrives in whatever encoding the source used, commonly Base64. contentDigest is the uppercase SHA-256 hexadecimal digest of the complete value and must be identical across every range of one read; a change means the record was re-imported mid-read, so restart at offset 0. An offset equal to totalLength returns empty content and a null nextOffset; a greater offset fails. Nothing is staged and no record is changed.")]
    public static Task<CallToolResult> ReadValueAsync(
        RemoteRecordSourceCommands commands, Guid domainId, Guid recordId, int ordinal,
        int offset = 0, int count = 16384, CancellationToken cancellationToken = default) =>
        commands.ReadValueAsync(domainId, recordId, ordinal, offset, count, cancellationToken);
}
