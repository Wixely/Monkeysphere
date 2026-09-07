using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

public sealed partial class RemoteRecordWriter
{
    public Task<CallToolResult> CreateDomainAsync(Guid domainId, string name, Guid idempotencyKey,
        CancellationToken cancellationToken) => RunAsync(domainId, "domains.create", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, name });
        RecordCommandIdentity identity = identities.Create(domainId, "domains.manage", "domains.create", idempotencyKey, hash);
        return await domainCommands.CreateAsync(identity, name, cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> RenameDomainAsync(Guid domainId, string name, string expectedRevision, Guid idempotencyKey,
        CancellationToken cancellationToken) => RunAsync(domainId, "domains.rename", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, name, expectedRevision });
        RecordCommandIdentity identity = identities.Create(domainId, "domains.manage", "domains.rename", idempotencyKey, hash);
        return await domainCommands.RenameAsync(identity, name, expectedRevision, cancellationToken).ConfigureAwait(false);
    });
}

[McpServerToolType]
[RemoteToolScopes("domains.manage")]
public sealed class MonkeysphereDomainWriteTools
{
    [McpServerTool(Name = "create_domain", ReadOnly = false, Destructive = false)]
    [Description("Creates an isolated blank domain. Requires domains.manage, a client-generated new non-Default domainId UUID, name and idempotencyKey UUID. Keep the same ID, key and name on retry. A durable reservation is resumed by retries or startup even after cancellation; pending domains are hidden until storage and the receipt are ready. Identical completed requests replay for 24 hours. Run setup separately after creation.")]
    public static Task<CallToolResult> CreateAsync(RemoteRecordWriter writer, Guid domainId, string name,
        Guid idempotencyKey, CancellationToken cancellationToken = default) => writer.CreateDomainAsync(domainId, name, idempotencyKey, cancellationToken);

    [McpServerTool(Name = "rename_domain", ReadOnly = false, Destructive = false)]
    [Description("Renames a domain, preserving its ID, contents and Default status. Requires domains.manage, explicit domainId, expectedRevision from domain discovery and idempotencyKey. Names are trimmed, nonempty, unique without regard to case and at most 100 characters. Mutation, receipt and audit commit in the domain registry; identical retries replay for 24 hours without repeating the rename.")]
    public static Task<CallToolResult> RenameAsync(RemoteRecordWriter writer, Guid domainId, string name, string expectedRevision,
        Guid idempotencyKey, CancellationToken cancellationToken = default) => writer.RenameDomainAsync(domainId, name, expectedRevision, idempotencyKey, cancellationToken);
}
