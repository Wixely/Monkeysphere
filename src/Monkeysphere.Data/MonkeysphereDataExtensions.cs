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
        services.TryAddScoped<ICurrentDomainScope, DefaultCurrentDomain>();
        services.TryAddScoped<ICurrentDomain>(provider => provider.GetRequiredService<ICurrentDomainScope>());
        services.AddSingleton<DomainRegistryConnectionFactory>();
        services.AddSingleton<DomainMigrationTarget>();
        services.AddSingleton<MonkeysphereMigrationConnectionFactory>();
        services.AddSingleton<IDomainDatabaseMigrator, DomainDatabaseMigrator>();
        services.AddSingleton<IDomainCatalog, DomainCatalog>();
        services.AddScoped<MonkeysphereConnectionFactory>();
        services.AddScoped<IMonkeysphereStore, SqliteMonkeysphereStore>();
        services.AddScoped<IMonkeysphereService, MonkeysphereService>();
        services.AddScoped<ICalendarStore, SqliteCalendarStore>();
        services.AddScoped<ICalendarService, CalendarService>();
        services.AddScoped<ISpatialMapStore, SqliteSpatialMapStore>();
        services.AddScoped<ISpatialMapService, SpatialMapService>();
        services.AddScoped<IReminderStore, SqliteReminderStore>();
        services.AddScoped<IReminderService, ReminderService>();
        services.AddScoped<IVCardStore, SqliteVCardStore>();
        services.AddScoped<IVCardService, VCardService>();
        services.AddScoped<IRecordImageService, RecordImageService>();
        services.AddScoped<IBackupService, BackupService>();
        services.AddScoped<IRelationshipStore, SqliteRelationshipStore>();
        services.AddScoped<IRelationshipService, RelationshipService>();
        services.AddScoped<IRelationshipGraphStore, SqliteRelationshipGraphStore>();
        services.AddScoped<IRelationshipGraphService, RelationshipGraphService>();
        services.AddScoped<ISavedViewStore, SqliteSavedViewStore>();
        services.AddScoped<ISavedViewService, SavedViewService>();
        services.AddScoped<IGraphViewStore, SqliteGraphViewStore>();
        services.AddScoped<IGraphViewService, GraphViewService>();
        services.AddScoped<IDashboardStore, SqliteDashboardStore>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IMapSettingsStore, SqliteMapSettingsStore>();
        services.AddScoped<IMapSettingsService, MapSettingsService>();
        services.AddScoped<IPresetStore, SqlitePresetStore>();
        services.AddScoped<IPresetService, PresetService>();
        services.AddScoped<IDebugDatabaseResetService, DebugDatabaseResetService>();
        services.AddDnaXDataMigrations(DatabaseName, options =>
        {
            options.ConnectionFactory = provider =>
                provider.GetRequiredService<MonkeysphereMigrationConnectionFactory>().CreateConnection();
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
        services.AddDnaXDataMigrations(DomainRegistrySchema.DatabaseName, options =>
        {
            options.ConnectionFactory = provider =>
                provider.GetRequiredService<DomainRegistryConnectionFactory>().CreateConnection();
            options.Manifest = DomainRegistrySchema.Manifest;
            options.ApplicationVersion = typeof(DomainRegistrySchema).Assembly.GetName().Version?.ToString();
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

    public static async Task InitializeMonkeysphereDomainsAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        await services.MigrateDnaXDatabaseAsync(DomainRegistrySchema.DatabaseName, cancellationToken).ConfigureAwait(false);
        IDomainCatalog catalog = services.GetRequiredService<IDomainCatalog>();
        await catalog.InitializeAsync(cancellationToken).ConfigureAwait(false);
        IDomainDatabaseMigrator databases = services.GetRequiredService<IDomainDatabaseMigrator>();
        foreach (MonkeysphereDomain domain in catalog.Snapshot)
        {
            await databases.MigrateAsync(domain.Id, cancellationToken).ConfigureAwait(false);
        }
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
    ICurrentDomain currentDomain,
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

        string mediaRoot = Path.GetFullPath(paths.ResolveWritable(DomainStoragePaths.MediaRelativeRoot(currentDomain.Id)));
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

public sealed class MonkeysphereConnectionFactory(IDnaXPaths paths, ICurrentDomain currentDomain)
{
    public string DatabasePath => paths.ResolveWritable(DomainStoragePaths.DatabaseRelativePath(currentDomain.Id));

    public SqliteConnection CreateConnection()
    {
        string databasePath = DatabasePath;
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
