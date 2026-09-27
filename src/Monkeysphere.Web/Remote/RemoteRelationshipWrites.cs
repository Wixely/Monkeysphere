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
        bool expired, DateTimeOffset? expiresAtUtc,
        Guid idempotencyKey, CancellationToken cancellationToken) => RunAsync(domainId, "relationships.create", async () =>
    {
        // The hash carries the expiry too. Without it, the same call with and without an ending
        // would replay each other's receipt and the second would silently do nothing.
        string hash = CommandRequestHash.Compute(new
        {
            contract = 2,
            domainId,
            typeId,
            sourceRecordId,
            targetRecordId,
            expectedTypeRevision,
            expectedSourceRevision,
            expectedTargetRevision,
            note,
            expired,
            expiresAtUtc,
        });
        RecordCommandIdentity identity = identities.Create(domainId, "relationships.write", "relationships.create", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await relationshipCommands.CreateAsync(identity, typeId, sourceRecordId, targetRecordId, expectedTypeRevision,
            expectedSourceRevision, expectedTargetRevision, note, Expiry(expired, expiresAtUtc), cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> UpdateRelationshipAsync(Guid domainId, Guid id, Guid typeId, string expectedRevision,
        string expectedTypeRevision, string? note, bool expired, DateTimeOffset? expiresAtUtc,
        Guid idempotencyKey, CancellationToken cancellationToken) => RunAsync(domainId, "relationships.update", async () =>
    {
        string hash = CommandRequestHash.Compute(new
        {
            contract = 1,
            domainId,
            id,
            typeId,
            expectedRevision,
            expectedTypeRevision,
            note,
            expired,
            expiresAtUtc,
        });
        RecordCommandIdentity identity = identities.Create(domainId, "relationships.write", "relationships.update", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await relationshipCommands.UpdateAsync(identity, id, typeId, expectedRevision, expectedTypeRevision, note,
            Expiry(expired, expiresAtUtc), cancellationToken).ConfigureAwait(false);
    });

    /// <summary>
    /// The flag wins where both are given, which is the same rule the browser applies and the same
    /// one <see cref="RelationshipExpiry.IsExpiredAt"/> resolves: an ending said outright never has
    /// to wait for a date to catch up with it.
    /// </summary>
    private static RelationshipExpiry Expiry(bool expired, DateTimeOffset? expiresAtUtc) =>
        new(expired, expiresAtUtc?.ToUniversalTime());

    public Task<CallToolResult> DeleteRelationshipAsync(Guid domainId, Guid id, string expectedRevision, Guid idempotencyKey,
        CancellationToken cancellationToken) => RunAsync(domainId, "relationships.delete", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, id, expectedRevision });
        RecordCommandIdentity identity = identities.Create(domainId, "relationships.write", "relationships.delete", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await relationshipCommands.DeleteAsync(identity, id, expectedRevision, cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> RenameRelationshipTypeAsync(Guid domainId, Guid typeId, string expectedRevision, string name,
        string? inverseName, Guid idempotencyKey, CancellationToken cancellationToken) =>
        RunAsync(domainId, "relationship_types.rename", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, typeId, expectedRevision, name, inverseName });
        RecordCommandIdentity identity = identities.Create(domainId, "structure.write", "relationship_types.rename", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await relationshipCommands.RenameTypeAsync(identity, typeId, expectedRevision, name, inverseName, cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> RetireRelationshipTypeAsync(Guid domainId, Guid typeId, string expectedRevision,
        Guid idempotencyKey, CancellationToken cancellationToken) => RunAsync(domainId, "relationship_types.retire", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, typeId, expectedRevision });
        RecordCommandIdentity identity = identities.Create(domainId, "structure.write", "relationship_types.retire", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await relationshipCommands.RetireTypeAsync(identity, typeId, expectedRevision, cancellationToken).ConfigureAwait(false);
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

    [RemoteToolScopes("structure.write")]
    [McpServerTool(Name = "rename_relationship_type", ReadOnly = false, Destructive = false)]
    [Description("Relabels a relationship definition. Requires structure.write, explicit domainId, expectedRevision from list_relationship_types and idempotencyKey. Labels are trimmed and at most 200 characters. A directional type needs an inverseName; a symmetric one has none, and one supplied for it is ignored, matching create_relationship_type. Directionality itself cannot be changed, because every existing relationship was recorded under it. A retired type can still be relabelled, since its relationships are still shown. Returns an updated ID/revision receipt with 24-hour identical retry replay.")]
    public static Task<CallToolResult> RenameTypeAsync(RemoteRecordWriter writer, Guid domainId, Guid typeId, string expectedRevision,
        string name, Guid idempotencyKey, string? inverseName = null, CancellationToken cancellationToken = default) =>
        writer.RenameRelationshipTypeAsync(domainId, typeId, expectedRevision, name, inverseName, idempotencyKey, cancellationToken);

    [RemoteToolScopes("structure.write")]
    [McpServerTool(Name = "retire_relationship_type", ReadOnly = false, Destructive = true)]
    [Description("Retires an active relationship definition. Requires structure.write, explicit domainId, expectedRevision from list_relationship_types and idempotencyKey. Relationships already recorded under it are kept and still shown; the type stops being offered for new ones, and create_relationship against it is refused. Returns a retired ID/revision receipt.")]
    public static Task<CallToolResult> RetireTypeAsync(RemoteRecordWriter writer, Guid domainId, Guid typeId, string expectedRevision,
        Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        writer.RetireRelationshipTypeAsync(domainId, typeId, expectedRevision, idempotencyKey, cancellationToken);

    [RemoteToolScopes("relationships.write")]
    [McpServerTool(Name = "create_relationship", ReadOnly = false, Destructive = false)]
    [Description("Links two distinct records in an explicit domain. Requires relationships.write, idempotencyKey and current revisions of the type and both records. Symmetric links canonicalize endpoints; duplicate links fail. Optional note is at most 2000 characters. Optional expired and expiresAtUtc record a link that has already ended or ends on a date; expired wins where both are given. Returns an ID/revision receipt; identical retries replay for 24 hours.")]
    public static Task<CallToolResult> CreateAsync(RemoteRecordWriter writer, Guid domainId, Guid typeId, Guid sourceRecordId, Guid targetRecordId,
        string expectedTypeRevision, string expectedSourceRevision, string expectedTargetRevision, Guid idempotencyKey,
        string? note = null, bool expired = false, DateTimeOffset? expiresAtUtc = null,
        CancellationToken cancellationToken = default) =>
        writer.CreateRelationshipAsync(domainId, typeId, sourceRecordId, targetRecordId, expectedTypeRevision,
            expectedSourceRevision, expectedTargetRevision, note, expired, expiresAtUtc, idempotencyKey, cancellationToken);

    [RemoteToolScopes("relationships.write")]
    [McpServerTool(Name = "update_relationship", ReadOnly = false, Destructive = false)]
    [Description("Changes one existing link in place, keeping its ID: its type, its note, and whether it has ended. Requires relationships.write, explicit domainId, expectedRevision from relationship reads, the current revision of the type being set, and idempotencyKey. It cannot move a link to different records — that is a different relationship, made by deleting this one and creating that. Omitting note clears it; expired and expiresAtUtc set the ending, and expired wins where both are given. Returns an ID/revision receipt; identical retries replay for 24 hours.")]
    public static Task<CallToolResult> UpdateAsync(RemoteRecordWriter writer, Guid domainId, Guid id, Guid typeId,
        string expectedRevision, string expectedTypeRevision, Guid idempotencyKey,
        string? note = null, bool expired = false, DateTimeOffset? expiresAtUtc = null,
        CancellationToken cancellationToken = default) =>
        writer.UpdateRelationshipAsync(domainId, id, typeId, expectedRevision, expectedTypeRevision, note, expired,
            expiresAtUtc, idempotencyKey, cancellationToken);

    [RemoteToolScopes("relationships.write")]
    [McpServerTool(Name = "delete_relationship", ReadOnly = false, Destructive = true)]
    [Description("Removes one link, preserving both records. Requires relationships.write, explicit domainId, expectedRevision from relationship reads, and idempotencyKey. Returns a deletion receipt; identical retries replay for 24 hours even after the link is gone.")]
    public static Task<CallToolResult> DeleteAsync(RemoteRecordWriter writer, Guid domainId, Guid id, string expectedRevision,
        Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        writer.DeleteRelationshipAsync(domainId, id, expectedRevision, idempotencyKey, cancellationToken);
}
