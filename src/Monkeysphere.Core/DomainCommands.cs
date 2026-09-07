namespace Monkeysphere.Core;

public static class DomainCommandLimits
{
    public const int MaximumRetainedCommands = 1000;
    public const int MaximumPendingCreations = 16;
}

public interface IDomainCommands
{
    Task<RecordCommandReceipt> CreateAsync(RecordCommandIdentity identity, string name,
        CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> RenameAsync(RecordCommandIdentity identity, string name, string expectedRevision,
        CancellationToken cancellationToken = default);
    Task RecordFailureAsync(ApplicationCommandEvent entry, CancellationToken cancellationToken = default);
}
