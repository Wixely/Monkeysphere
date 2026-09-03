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
        services.TryAddSingleton(new DebugResetAvailability(false));
        services.AddSingleton<MonkeysphereConnectionFactory>();
        services.AddSingleton<IMonkeysphereStore, SqliteMonkeysphereStore>();
        services.AddSingleton<IMonkeysphereService, MonkeysphereService>();
        services.AddSingleton<ICalendarStore, SqliteCalendarStore>();
        services.AddSingleton<ICalendarService, CalendarService>();
        services.AddSingleton<ISpatialMapStore, SqliteSpatialMapStore>();
        services.AddSingleton<ISpatialMapService, SpatialMapService>();
        services.AddSingleton<IReminderStore, SqliteReminderStore>();
        services.AddSingleton<IReminderService, ReminderService>();
        services.AddSingleton<IVCardStore, SqliteVCardStore>();
        services.AddSingleton<IVCardService, VCardService>();
        services.AddSingleton<IRecordImageService, RecordImageService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IRelationshipStore, SqliteRelationshipStore>();
        services.AddSingleton<IRelationshipService, RelationshipService>();
        services.AddSingleton<IRelationshipGraphStore, SqliteRelationshipGraphStore>();
        services.AddSingleton<IRelationshipGraphService, RelationshipGraphService>();
        services.AddSingleton<ISavedViewStore, SqliteSavedViewStore>();
        services.AddSingleton<ISavedViewService, SavedViewService>();
        services.AddSingleton<IGraphViewStore, SqliteGraphViewStore>();
        services.AddSingleton<IGraphViewService, GraphViewService>();
        services.AddSingleton<IDashboardStore, SqliteDashboardStore>();
        services.AddSingleton<IDashboardService, DashboardService>();
        services.AddSingleton<IMapSettingsStore, SqliteMapSettingsStore>();
        services.AddSingleton<IMapSettingsService, MapSettingsService>();
        services.AddSingleton<IPresetStore, SqlitePresetStore>();
        services.AddSingleton<IPresetService, PresetService>();
        services.AddSingleton<IDebugDatabaseResetService, DebugDatabaseResetService>();
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

public interface IDebugDatabaseResetService
{
    Task ResetAsync(CancellationToken cancellationToken = default);
}

public sealed record DebugResetAvailability(bool Enabled);

internal sealed class DebugDatabaseResetService(
    MonkeysphereConnectionFactory connections,
    IDnaXPaths paths,
    DebugResetAvailability availability) : IDebugDatabaseResetService
{
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        if (!availability.Enabled)
        {
            throw new InvalidOperationException("Database reset is not enabled by deployment configuration.");
        }

        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM DashboardRecurringFields;
            DELETE FROM DashboardCategories;
            DELETE FROM DashboardSettings;
            DELETE FROM MapSettings;
            DELETE FROM GraphViews;
            DELETE FROM SavedViews;
            DELETE FROM Relationships;
            DELETE FROM RelationshipTypes;
            DELETE FROM VCardProperties;
            DELETE FROM VCardImports;
            DELETE FROM Reminders;
            DELETE FROM RecordImages;
            DELETE FROM RecordAliases;
            DELETE FROM FieldValueLocations;
            DELETE FROM FieldValueTags;
            DELETE FROM FieldValues;
            DELETE FROM Records;
            DELETE FROM RecordTypeFields;
            DELETE FROM FieldDefinitions;
            DELETE FROM RecordTypes;
            DELETE FROM SetupState;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        string mediaRoot = Path.GetFullPath(paths.ResolveWritable(Path.Combine("media", "records")));
        string writableRoot = Path.GetFullPath(paths.ResolveWritable("."));
        if (!mediaRoot.StartsWith(Path.TrimEndingDirectorySeparator(writableRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The record media directory is outside the writable data root.");
        }
        if (Directory.Exists(mediaRoot))
        {
            Directory.Delete(mediaRoot, recursive: true);
        }
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
