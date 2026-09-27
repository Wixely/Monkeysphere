namespace Monkeysphere.Core;

public sealed record RecordCommandIdentity(Guid DomainId, string Surface, string CredentialFingerprint, string Action, Guid IdempotencyKey, string RequestHash)
{
    /// <summary>
    /// Every action a receipt can be stored against. Closed rather than free text because the action
    /// is part of what makes a retry key unique, so a typo would otherwise open a second idempotency
    /// space silently. A new command has to be added here as well as implemented.
    /// </summary>
    private static readonly HashSet<string> KnownActions =
    [
        "records.create", "records.patch", "records.batch", "records.delete",
        "relationships.create", "relationships.update", "relationships.delete", "relationship_types.create",
        "relationship_types.rename", "relationship_types.retire",
        "record_types.create", "record_types.update", "record_types.retire", "record_types.merge",
        "fields.create_attach", "fields.attach", "fields.rename", "fields.retire", "fields.merge", "fields.convert",
        "presets.install", "setup.complete", "domains.rename", "domains.create",
    ];

    public void Validate()
    {
        if (DomainId == Guid.Empty || IdempotencyKey == Guid.Empty || Surface is not ("mcp" or "api") ||
            !KnownActions.Contains(Action) || !IsDigest(CredentialFingerprint) || !IsDigest(RequestHash))
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
