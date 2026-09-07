namespace Monkeysphere.Core;

public sealed record RecordCommandIdentity(Guid DomainId, string Surface, string CredentialFingerprint, string Action, Guid IdempotencyKey, string RequestHash)
{
    public void Validate()
    {
        if (DomainId == Guid.Empty || IdempotencyKey == Guid.Empty || Surface is not ("mcp" or "api") ||
            Action is not ("records.create" or "records.patch" or "records.batch" or "records.delete" or
                "relationships.create" or "relationships.delete" or "relationship_types.create" or
                "record_types.create" or "fields.create_attach" or "fields.attach" or "presets.install" or "setup.complete" or "domains.rename") || !IsDigest(CredentialFingerprint) || !IsDigest(RequestHash))
        {
            throw new DomainValidationException("The command identity is invalid.");
        }
    }

    private static bool IsDigest(string value) => value is { Length: 64 } && value.All(character => char.IsAsciiHexDigit(character));
}

public enum RecordMutationKind { Create, Replace }

public sealed record PreparedRecordMutation(RecordMutationKind Kind, Guid Id, PreparedRecord Record, string? ExpectedRevision = null);
public sealed record RecordMutationOutcome(Guid Id, string Revision, string Outcome);
public sealed record RecordCommandReceipt(Guid IdempotencyKey, IReadOnlyList<RecordMutationOutcome> Items, DateTimeOffset CompletedAtUtc, DateTimeOffset RetryUntilUtc);

public sealed class CommandReplayException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class RecordCommandNotFoundException(string message) : InvalidOperationException(message);

public interface IRecordCommandStore
{
    Task<RecordCommandReceipt?> GetReceiptAsync(RecordCommandIdentity identity, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> ExecuteAsync(RecordCommandIdentity identity, IReadOnlyList<PreparedRecordMutation> mutations,
        DateTimeOffset now, CancellationToken cancellationToken = default);
}
