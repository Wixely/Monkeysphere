using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

public sealed partial class RemoteRecordWriter
{
    public Task<CallToolResult> CreateRelationshipTypeAsync(Guid domainId, string name, string directionality, string? inverseName,
        Guid idempotencyKey, CancellationToken cancellationToken) => RunAsync(domainId, "relationship_types.create", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, name, directionality, inverseName });
        RecordCommandIdentity identity = identities.Create(domainId, "structure.write", "relationship_types.create", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        if (!Enum.TryParse(directionality, ignoreCase: true, out RelationshipDirectionality direction) || !Enum.IsDefined(direction) ||
            !string.Equals(direction.ToString(), directionality, StringComparison.OrdinalIgnoreCase))
            throw new DomainValidationException("Directionality must be directional or symmetric.");
        return await relationshipCommands.CreateTypeAsync(identity, new(name, direction, inverseName), cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> CreateRelationshipAsync(Guid domainId, Guid typeId, Guid sourceRecordId, Guid targetRecordId,
        string expectedTypeRevision, string expectedSourceRevision, string expectedTargetRevision, string? note,
        Guid idempotencyKey, CancellationToken cancellationToken) => RunAsync(domainId, "relationships.create", async () =>
    {
        string hash = CommandRequestHash.Compute(new
        {
            contract = 1,
            domainId,
            typeId,
            sourceRecordId,
            targetRecordId,
            expectedTypeRevision,
            expectedSourceRevision,
            expectedTargetRevision,
            note,
        });
        RecordCommandIdentity identity = identities.Create(domainId, "relationships.write", "relationships.create", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await relationshipCommands.CreateAsync(identity, typeId, sourceRecordId, targetRecordId, expectedTypeRevision,
            expectedSourceRevision, expectedTargetRevision, note, cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> DeleteRelationshipAsync(Guid domainId, Guid id, string expectedRevision, Guid idempotencyKey,
        CancellationToken cancellationToken) => RunAsync(domainId, "relationships.delete", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, id, expectedRevision });
        RecordCommandIdentity identity = identities.Create(domainId, "relationships.write", "relationships.delete", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await relationshipCommands.DeleteAsync(identity, id, expectedRevision, cancellationToken).ConfigureAwait(false);
    });
}

[McpServerToolType]
public sealed class MonkeysphereRelationshipWriteTools
{
    [RemoteToolScopes("structure.write")]
    [McpServerTool(Name = "create_relationship_type", ReadOnly = false, Destructive = false)]
    [Description("Creates a relationship definition in an explicit domain. Requires structure.write and idempotencyKey. Directionality is directional (requires inverseName) or symmetric (inverseName is ignored). Labels are at most 200 characters. Returns an ID/revision receipt; identical retries replay for 24 hours.")]
    public static Task<CallToolResult> CreateTypeAsync(RemoteRecordWriter writer, Guid domainId, string name, string directionality,
        Guid idempotencyKey, string? inverseName = null, CancellationToken cancellationToken = default) =>
        writer.CreateRelationshipTypeAsync(domainId, name, directionality, inverseName, idempotencyKey, cancellationToken);

    [RemoteToolScopes("relationships.write")]
    [McpServerTool(Name = "create_relationship", ReadOnly = false, Destructive = false)]
    [Description("Links two distinct records in an explicit domain. Requires relationships.write, idempotencyKey and current revisions of the type and both records. Symmetric links canonicalize endpoints; duplicate links fail. Optional note is at most 2000 characters. Returns an ID/revision receipt; identical retries replay for 24 hours.")]
    public static Task<CallToolResult> CreateAsync(RemoteRecordWriter writer, Guid domainId, Guid typeId, Guid sourceRecordId, Guid targetRecordId,
        string expectedTypeRevision, string expectedSourceRevision, string expectedTargetRevision, Guid idempotencyKey,
        string? note = null, CancellationToken cancellationToken = default) =>
        writer.CreateRelationshipAsync(domainId, typeId, sourceRecordId, targetRecordId, expectedTypeRevision,
            expectedSourceRevision, expectedTargetRevision, note, idempotencyKey, cancellationToken);

    [RemoteToolScopes("relationships.write")]
    [McpServerTool(Name = "delete_relationship", ReadOnly = false, Destructive = true)]
    [Description("Removes one link, preserving both records. Requires relationships.write, explicit domainId, expectedRevision from relationship reads, and idempotencyKey. Returns a deletion receipt; identical retries replay for 24 hours even after the link is gone.")]
    public static Task<CallToolResult> DeleteAsync(RemoteRecordWriter writer, Guid domainId, Guid id, string expectedRevision,
        Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        writer.DeleteRelationshipAsync(domainId, id, expectedRevision, idempotencyKey, cancellationToken);
}
