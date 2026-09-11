using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

/// <summary>
/// Backstage activations live in the domain registry rather than in a domain database: an account
/// stands backstage for the deployment, not for one domain at a time, and hidden records exist in
/// every domain the account can reach.
/// </summary>
internal sealed class SqliteBackstageSessionStore(DomainRegistryConnectionFactory connections) : IBackstageSessionStore
{
    public async Task<BackstageSession?> GetAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SessionRow? row = await connection.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition(
            "SELECT AccountId, ActivatedAtUtc, ExpiresAtUtc FROM BackstageSessions WHERE AccountId = @AccountId;",
            new { AccountId = accountId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : new(row.AccountId, Parse(row.ActivatedAtUtc), Parse(row.ExpiresAtUtc));
    }

    public async Task<BackstageSession> ActivateAsync(string accountId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        // Re-activating restarts the clock rather than extending an old one, so the recorded
        // expiry always means "24 hours from when this account last chose to be backstage".
        BackstageSession session = new(accountId, now, now.AddHours(BackstageSession.LifetimeHours));
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO BackstageSessions (AccountId, ActivatedAtUtc, ExpiresAtUtc)
            VALUES (@AccountId, @ActivatedAtUtc, @ExpiresAtUtc)
            ON CONFLICT (AccountId) DO UPDATE SET
                ActivatedAtUtc = excluded.ActivatedAtUtc,
                ExpiresAtUtc = excluded.ExpiresAtUtc;
            """,
            new
            {
                session.AccountId,
                ActivatedAtUtc = Timestamp(session.ActivatedAtUtc),
                ExpiresAtUtc = Timestamp(session.ExpiresAtUtc),
            }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return session;
    }

    public async Task DeactivateAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM BackstageSessions WHERE AccountId = @AccountId;",
            new { AccountId = accountId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BackstageSession>> ListActiveAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<SessionRow> rows = await connection.QueryAsync<SessionRow>(new CommandDefinition(
            "SELECT AccountId, ActivatedAtUtc, ExpiresAtUtc FROM BackstageSessions WHERE ExpiresAtUtc > @Now;",
            new { Now = Timestamp(now) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(row => new BackstageSession(row.AccountId, Parse(row.ActivatedAtUtc), Parse(row.ExpiresAtUtc))).ToArray();
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM BackstageSessions WHERE ExpiresAtUtc <= @Now;",
            new { Now = Timestamp(now) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class SessionRow
    {
        public required string AccountId { get; init; }
        public required string ActivatedAtUtc { get; init; }
        public required string ExpiresAtUtc { get; init; }
    }
}

/// <summary>
/// The backstage state of a record. Setting it deliberately leaves the record's revision alone:
/// hiding a record is a policy change about who may see it, not an edit to what it says, so it
/// must not make every open editor's pending save conflict.
/// </summary>
internal sealed class SqliteBackstageRecordStore(MonkeysphereConnectionFactory connections) : IBackstageRecordStore
{
    public async Task SetStateAsync(Guid recordId, string? state, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Records SET BackstageState = @State WHERE Id = @Id;",
            new { Id = recordId.ToString("D", CultureInfo.InvariantCulture), State = state },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new DomainValidationException("The record was not found.");
        }
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM Records WHERE BackstageState IS NOT NULL;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
