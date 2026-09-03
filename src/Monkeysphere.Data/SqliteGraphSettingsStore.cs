using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed class SqliteGraphSettingsStore(MonkeysphereConnectionFactory connections) : IGraphSettingsStore
{
    public async Task<GraphConfiguration> GetAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int? enabled = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition("""
            SELECT WarnUnsavedChanges
            FROM GraphSettings
            WHERE Singleton = 1;
            """, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new(enabled is null or 1);
    }

    public async Task SaveAsync(
        GraphConfiguration configuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO GraphSettings (Singleton, WarnUnsavedChanges, UpdatedAtUtc)
            VALUES (1, @WarnUnsavedChanges, @UpdatedAtUtc)
            ON CONFLICT (Singleton) DO UPDATE SET
                WarnUnsavedChanges = excluded.WarnUnsavedChanges,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """, new
        {
            WarnUnsavedChanges = configuration.WarnUnsavedChanges ? 1 : 0,
            UpdatedAtUtc = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
