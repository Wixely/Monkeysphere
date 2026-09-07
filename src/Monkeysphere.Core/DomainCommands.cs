namespace Monkeysphere.Core;

public static class DomainCommandLimits
{
    public const int MaximumRetainedCommands = 1000;
}

public interface IDomainCommands
{
    Task<RecordCommandReceipt> RenameAsync(RecordCommandIdentity identity, string name, string expectedRevision,
        CancellationToken cancellationToken = default);
    Task RecordFailureAsync(ApplicationCommandEvent entry, CancellationToken cancellationToken = default);
}
