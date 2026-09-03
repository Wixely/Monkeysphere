using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed class SqliteMapSettingsStore(MonkeysphereConnectionFactory connections) : IMapSettingsStore
{
    public async Task<MapConfiguration> GetAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int? enabled = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition("""
            SELECT ExternalTilesEnabled
            FROM MapSettings
            WHERE Singleton = 1;
            """, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new(enabled == 1);
    }

    public async Task SaveAsync(
        MapConfiguration configuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO MapSettings (Singleton, ExternalTilesEnabled, UpdatedAtUtc)
            VALUES (1, @ExternalTilesEnabled, @UpdatedAtUtc)
            ON CONFLICT (Singleton) DO UPDATE SET
                ExternalTilesEnabled = excluded.ExternalTilesEnabled,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """, new
        {
            ExternalTilesEnabled = configuration.ExternalTilesEnabled ? 1 : 0,
            UpdatedAtUtc = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
