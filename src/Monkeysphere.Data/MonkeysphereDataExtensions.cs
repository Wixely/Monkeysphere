using DnaX.Data.Migrations;
using DnaX.Data.Migrations.Sqlite;
using DnaX.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public static class MonkeysphereDataExtensions
{
    public const string DatabaseName = "Monkeysphere";

    public static IServiceCollection AddMonkeysphereData(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<MonkeysphereConnectionFactory>();
        services.AddSingleton<IMonkeysphereStore, SqliteMonkeysphereStore>();
        services.AddSingleton<IMonkeysphereService, MonkeysphereService>();
        services.AddSingleton<IRelationshipStore, SqliteRelationshipStore>();
        services.AddSingleton<IRelationshipService, RelationshipService>();
        services.AddSingleton<ISavedViewStore, SqliteSavedViewStore>();
        services.AddSingleton<ISavedViewService, SavedViewService>();
        services.AddDnaXDataMigrations(DatabaseName, options =>
        {
            options.ConnectionFactory = provider =>
                provider.GetRequiredService<MonkeysphereConnectionFactory>().CreateConnection();
            options.Manifest = MonkeysphereSchema.Manifest;
            options.ApplicationVersion = typeof(MonkeysphereSchema).Assembly.GetName().Version?.ToString();
            options.UseSqlite(sqlite =>
            {
                sqlite.EnableWriteAheadLogging = true;
                sqlite.EnforceForeignKeys = true;
                sqlite.DeferForeignKeysDuringMigration = true;
                sqlite.LockTimeout = TimeSpan.FromSeconds(30);
            });
        });
        return services;
    }
}

public sealed class MonkeysphereConnectionFactory(IDnaXPaths paths)
{
    public SqliteConnection CreateConnection()
    {
        string databasePath = paths.ResolveWritable("monkeysphere.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 30,
        };
        return new SqliteConnection(builder.ConnectionString);
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
