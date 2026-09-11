namespace Monkeysphere.Core;

/// <summary>
/// Backstage policy states a record can carry. Null means an ordinary record. The set is
/// deliberately extensible: <see cref="Hidden"/> is the first state, and further states are
/// expected to describe other backstage-only treatments of a record.
/// </summary>
public static class BackstageStates
{
    /// <summary>Withheld from every ordinary read. Only a backstage caller can observe it at all.</summary>
    public const string Hidden = "hidden";

    public static readonly IReadOnlyList<string> All = [Hidden];

    /// <summary>Validates a state, or null to clear it.</summary>
    public static string? Normalize(string? state) =>
        state is null || All.Contains(state, StringComparer.Ordinal)
            ? state
            : throw new DomainValidationException($"Backstage state must be null or one of: {string.Join(", ", All)}.");
}

/// <summary>
/// Whether the deployment offers backstage at all. Off unless the operator opts in, and a true
/// kill switch: with it off nothing observes a hidden record, not the browser and not a remote
/// credential holding the backstage grant. Existing hidden records stay hidden.
/// </summary>
public sealed record BackstageAvailability(bool Enabled);

/// <summary>
/// One account's backstage activation. Time-boxed so that forgetting to leave backstage cannot
/// leave records exposed indefinitely.
/// </summary>
public sealed record BackstageSession(string AccountId, DateTimeOffset ActivatedAtUtc, DateTimeOffset ExpiresAtUtc)
{
    public const int LifetimeHours = 24;

    public bool IsActiveAt(DateTimeOffset now) => now < ExpiresAtUtc;
}

/// <summary>
/// The single question every read path asks. Implementations must default to withholding: a caller
/// that cannot prove backstage authority sees ordinary records only.
/// </summary>
public interface IBackstageVisibility
{
    /// <summary>True only when the caller may observe records carrying a backstage state.</summary>
    bool IncludeBackstageRecords { get; }
}

/// <summary>Withholds backstage records. Used wherever no backstage authority has been established.</summary>
public sealed class OrdinaryVisibility : IBackstageVisibility
{
    public static readonly OrdinaryVisibility Instance = new();

    public bool IncludeBackstageRecords => false;
}

/// <summary>
/// The SQL every read of Records must carry. Centralised so the rule is stated once and so a
/// reviewer can find every caller; the exhaustive leak test is what proves none was missed.
/// </summary>
public static class BackstageFilter
{
    /// <summary>
    /// Returns the predicate to append inside an existing WHERE or JOIN condition, or an empty
    /// string for a backstage caller. Pass the table alias used by the query, or an empty string
    /// when the query selects from Records without one.
    /// </summary>
    public static string AndVisible(IBackstageVisibility visibility, string alias = "")
    {
        ArgumentNullException.ThrowIfNull(visibility);
        if (visibility.IncludeBackstageRecords) return string.Empty;
        string prefix = string.IsNullOrEmpty(alias) ? string.Empty : alias + ".";
        return $" AND {prefix}BackstageState IS NULL";
    }

    /// <summary>The same predicate as a complete WHERE clause for queries that have no other filter.</summary>
    public static string WhereVisible(IBackstageVisibility visibility, string alias = "")
    {
        string predicate = AndVisible(visibility, alias);
        return predicate.Length == 0 ? string.Empty : " WHERE" + predicate[4..];
    }
}

/// <summary>
/// Who is asking. Today the browser has a single administrator account; the identifier is a string
/// so that per-account backstage needs no change to storage or to this contract when it arrives.
/// </summary>
public interface IBackstageAccount
{
    /// <summary>The current account, or null when the caller is not an account holder.</summary>
    string? AccountId { get; }

    /// <summary>
    /// True when the caller presents a credential granted backstage access directly, rather than
    /// holding a time-boxed session. Deliberately not expiring: revoking the grant is the control.
    /// </summary>
    bool HasBackstageGrant { get; }
}

/// <summary>Backstage activations, shared across every domain of a deployment.</summary>
public interface IBackstageSessionStore
{
    Task<BackstageSession?> GetAsync(string accountId, CancellationToken cancellationToken = default);

    Task<BackstageSession> ActivateAsync(string accountId, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task DeactivateAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Every activation still running at <paramref name="now"/>.</summary>
    Task<IReadOnlyList<BackstageSession>> ListActiveAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Removes activations that have run out. Storage never relies on this for correctness.</summary>
    Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}

/// <summary>The backstage state carried by records of the current domain.</summary>
public interface IBackstageRecordStore
{
    Task SetStateAsync(Guid recordId, string? state, CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);
}

public sealed record BackstageStatus(bool Available, bool Active, DateTimeOffset? ExpiresAtUtc);

/// <summary>Reads and changes the backstage state of records. Requires backstage authority.</summary>
public interface IBackstageService
{
    /// <summary>Whether the deployment offers backstage and the caller currently holds it.</summary>
    Task<BackstageStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<BackstageStatus> ActivateAsync(CancellationToken cancellationToken = default);

    Task<BackstageStatus> DeactivateAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies or clears a record's backstage state. Null clears it.</summary>
    Task SetRecordStateAsync(Guid recordId, string? state, CancellationToken cancellationToken = default);

    /// <summary>Counts records carrying any backstage state, for operator warnings.</summary>
    Task<int> CountBackstageRecordsAsync(CancellationToken cancellationToken = default);
}

public sealed class BackstageService(
    BackstageAvailability availability,
    IBackstageAccount account,
    IBackstageSessionStore sessions,
    IBackstageRecordStore records,
    IBackstageVisibility visibility,
    TimeProvider timeProvider) : IBackstageService
{
    public async Task<BackstageStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!availability.Enabled || account.AccountId is not string accountId)
        {
            return new(false, false, null);
        }

        BackstageSession? session = await sessions.GetAsync(accountId, cancellationToken).ConfigureAwait(false);
        return session is not null && session.IsActiveAt(timeProvider.GetUtcNow())
            ? new(true, true, session.ExpiresAtUtc)
            : new(true, false, null);
    }

    public async Task<BackstageStatus> ActivateAsync(CancellationToken cancellationToken = default)
    {
        string accountId = RequireAvailableAccount();
        BackstageSession session = await sessions.ActivateAsync(accountId, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return new(true, true, session.ExpiresAtUtc);
    }

    public async Task<BackstageStatus> DeactivateAsync(CancellationToken cancellationToken = default)
    {
        string accountId = RequireAvailableAccount();
        await sessions.DeactivateAsync(accountId, cancellationToken).ConfigureAwait(false);
        return new(true, false, null);
    }

    public Task SetRecordStateAsync(Guid recordId, string? state, CancellationToken cancellationToken = default)
    {
        // Changing a record's state requires standing backstage, not merely the ability to reach
        // the record: an ordinary caller must not be able to hide a record from everyone else.
        if (!availability.Enabled || !visibility.IncludeBackstageRecords)
        {
            throw new DomainValidationException("Changing a record's backstage state requires backstage.");
        }

        return records.SetStateAsync(recordId, BackstageStates.Normalize(state), cancellationToken);
    }

    public Task<int> CountBackstageRecordsAsync(CancellationToken cancellationToken = default) =>
        records.CountAsync(cancellationToken);

    private string RequireAvailableAccount()
    {
        if (!availability.Enabled)
        {
            throw new DomainValidationException("Backstage is not enabled for this deployment.");
        }

        return account.AccountId
            ?? throw new DomainValidationException("Backstage requires a signed-in account.");
    }
}

/// <summary>No account, no grant. The default wherever a host has not established who is asking.</summary>
public sealed class NoBackstageAccount : IBackstageAccount
{
    public static readonly NoBackstageAccount Instance = new();

    public string? AccountId => null;

    public bool HasBackstageGrant => false;
}

/// <summary>
/// Keeps the active backstage activations in memory so that a read path, which must answer
/// "is this caller backstage?" synchronously, never has to touch storage. Monkeysphere is a single
/// self-hosted process, so routing every activation and deactivation through here keeps the cache
/// exact rather than eventually consistent. It starts cold and answers "not backstage" until it is
/// loaded, so a failure to warm it withholds records rather than exposing them.
/// </summary>
public sealed class CachedBackstageSessions(IBackstageSessionStore inner, TimeProvider timeProvider) : IBackstageSessionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _expiry = new(StringComparer.Ordinal);

    /// <summary>Loads the stored activations, discarding any that have already run out.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await inner.PurgeExpiredAsync(now, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<BackstageSession> active = await inner.ListActiveAsync(now, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _expiry.Clear();
            foreach (BackstageSession session in active)
            {
                _expiry[session.AccountId] = session.ExpiresAtUtc;
            }
        }
    }

    public bool IsActive(string? accountId)
    {
        if (accountId is null) return false;
        lock (_gate)
        {
            return _expiry.TryGetValue(accountId, out DateTimeOffset expiry) && timeProvider.GetUtcNow() < expiry;
        }
    }

    public Task<BackstageSession?> GetAsync(string accountId, CancellationToken cancellationToken = default) =>
        inner.GetAsync(accountId, cancellationToken);

    public Task<IReadOnlyList<BackstageSession>> ListActiveAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
        inner.ListActiveAsync(now, cancellationToken);

    public async Task<BackstageSession> ActivateAsync(string accountId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        BackstageSession session = await inner.ActivateAsync(accountId, now, cancellationToken).ConfigureAwait(false);
        lock (_gate) _expiry[session.AccountId] = session.ExpiresAtUtc;
        return session;
    }

    public async Task DeactivateAsync(string accountId, CancellationToken cancellationToken = default)
    {
        // Forget first: if the delete fails, the caller has still left backstage in this process.
        lock (_gate) _expiry.Remove(accountId);
        await inner.DeactivateAsync(accountId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (string accountId in _expiry.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray())
            {
                _expiry.Remove(accountId);
            }
        }

        return await inner.PurgeExpiredAsync(now, cancellationToken).ConfigureAwait(false);
    }
}
