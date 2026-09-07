using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

public sealed partial class RemoteRecordWriter
{
    public Task<CallToolResult> CreateRecordTypeAsync(Guid domainId, string name, string? symbol, Guid idempotencyKey,
        CancellationToken cancellationToken) => RunAsync(domainId, "record_types.create", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, name, symbol });
        RecordCommandIdentity identity = identities.Create(domainId, "structure.write", "record_types.create", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await structureCommands.CreateTypeAsync(identity, name, symbol, cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> CreateFieldAsync(Guid domainId, Guid recordTypeId, string expectedRevision, string name, string typeId,
        bool isRequired, IReadOnlyList<string>? choiceOptions, Guid idempotencyKey, CancellationToken cancellationToken) =>
        RunAsync(domainId, "fields.create_attach", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, recordTypeId, expectedRevision, name, typeId, isRequired, choiceOptions });
        RecordCommandIdentity identity = identities.Create(domainId, "structure.write", "fields.create_attach", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await structureCommands.CreateFieldAsync(identity, recordTypeId, expectedRevision,
            new(name, typeId, isRequired, choiceOptions), cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> AttachFieldAsync(Guid domainId, Guid recordTypeId, Guid fieldDefinitionId,
        string expectedRevision, string expectedFieldRevision, bool isRequired, Guid idempotencyKey, CancellationToken cancellationToken) =>
        RunAsync(domainId, "fields.attach", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, recordTypeId, fieldDefinitionId, expectedRevision, expectedFieldRevision, isRequired });
        RecordCommandIdentity identity = identities.Create(domainId, "structure.write", "fields.attach", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await structureCommands.AttachFieldAsync(identity, recordTypeId, fieldDefinitionId, expectedRevision,
            expectedFieldRevision, isRequired, cancellationToken).ConfigureAwait(false);
    });
}

[McpServerToolType]
[RemoteToolScopes("structure.write")]
public sealed class MonkeysphereStructureWriteTools
{
    [McpServerTool(Name = "create_record_type", ReadOnly = false, Destructive = false)]
    [Description("Creates a custom record type in an explicit domain. Requires structure.write and idempotencyKey. Name is trimmed and limited to 200 characters; optional symbol allows at most four visible characters/emoji and 32 UTF-16 units. Returns a type ID/revision receipt with 24-hour identical retry replay.")]
    public static Task<CallToolResult> CreateTypeAsync(RemoteRecordWriter writer, Guid domainId, string name, Guid idempotencyKey,
        string? symbol = null, CancellationToken cancellationToken = default) =>
        writer.CreateRecordTypeAsync(domainId, name, symbol, idempotencyKey, cancellationToken);

    [McpServerTool(Name = "create_and_attach_field", ReadOnly = false, Destructive = false)]
    [Description("Creates a reusable field and appends it to an active record type. Requires structure.write, explicit domainId, expectedRevision from get_record_type and idempotencyKey. Uses Core field type/configuration rules; choice fields require 1-1000 distinct options (200 characters each). Required attachments fail if existing records lack a value. Receipt contains created field ID/revision followed by updated record type ID/revision.")]
    public static Task<CallToolResult> CreateFieldAsync(RemoteRecordWriter writer, Guid domainId, Guid recordTypeId, string expectedRevision,
        string name, string typeId, Guid idempotencyKey, bool isRequired = false, IReadOnlyList<string>? choiceOptions = null,
        CancellationToken cancellationToken = default) =>
        writer.CreateFieldAsync(domainId, recordTypeId, expectedRevision, name, typeId, isRequired, choiceOptions, idempotencyKey, cancellationToken);

    [McpServerTool(Name = "attach_field", ReadOnly = false, Destructive = false)]
    [Description("Appends an existing active field to an active record type. Requires structure.write, explicit domainId, expectedRevision of the record type, expectedFieldRevision from field discovery and idempotencyKey. Duplicate attachments fail. Required attachments fail when any existing record lacks a value. Returns an updated type ID/revision receipt with 24-hour replay.")]
    public static Task<CallToolResult> AttachFieldAsync(RemoteRecordWriter writer, Guid domainId, Guid recordTypeId, Guid fieldDefinitionId,
        string expectedRevision, string expectedFieldRevision, Guid idempotencyKey, bool isRequired = false, CancellationToken cancellationToken = default) =>
        writer.AttachFieldAsync(domainId, recordTypeId, fieldDefinitionId, expectedRevision, expectedFieldRevision, isRequired, idempotencyKey, cancellationToken);
}
