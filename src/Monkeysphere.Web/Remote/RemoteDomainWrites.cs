using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

public sealed partial class RemoteRecordWriter
{
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
    [McpServerTool(Name = "rename_domain", ReadOnly = false, Destructive = false)]
    [Description("Renames a domain, preserving its ID, contents and Default status. Requires domains.manage, explicit domainId, expectedRevision from domain discovery and idempotencyKey. Names are trimmed, nonempty, unique without regard to case and at most 100 characters. Mutation, receipt and audit commit in the domain registry; identical retries replay for 24 hours without repeating the rename.")]
    public static Task<CallToolResult> RenameAsync(RemoteRecordWriter writer, Guid domainId, string name, string expectedRevision,
        Guid idempotencyKey, CancellationToken cancellationToken = default) => writer.RenameDomainAsync(domainId, name, expectedRevision, idempotencyKey, cancellationToken);
}
