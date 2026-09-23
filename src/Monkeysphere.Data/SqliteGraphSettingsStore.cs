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
        GraphSettingsRow? row = await connection.QuerySingleOrDefaultAsync<GraphSettingsRow>(new CommandDefinition("""
            SELECT WarnUnsavedChanges, NodeLimit, EdgeLimit
            FROM GraphSettings
            WHERE Singleton = 1;
            """, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null
            ? new GraphConfiguration()
            // Clamped on the way out: the row is storage, not a form, so a value from an older
            // build or an edited database must still leave the graph able to draw.
            : GraphConfiguration.Clamped(row.WarnUnsavedChanges == 1, (int)row.NodeLimit, (int)row.EdgeLimit);
    }

    public async Task SaveAsync(
        GraphConfiguration configuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO GraphSettings (Singleton, WarnUnsavedChanges, NodeLimit, EdgeLimit, UpdatedAtUtc)
            VALUES (1, @WarnUnsavedChanges, @NodeLimit, @EdgeLimit, @UpdatedAtUtc)
            ON CONFLICT (Singleton) DO UPDATE SET
                WarnUnsavedChanges = excluded.WarnUnsavedChanges,
                NodeLimit = excluded.NodeLimit,
                EdgeLimit = excluded.EdgeLimit,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """, new
        {
            WarnUnsavedChanges = configuration.WarnUnsavedChanges ? 1 : 0,
            configuration.NodeLimit,
            configuration.EdgeLimit,
            UpdatedAtUtc = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    // SQLite hands INTEGER columns back as 64-bit, and Dapper matches the constructor by exact
    // type, so these are long here and narrowed above rather than declared as int.
    private sealed record GraphSettingsRow(long WarnUnsavedChanges, long NodeLimit, long EdgeLimit);
}
