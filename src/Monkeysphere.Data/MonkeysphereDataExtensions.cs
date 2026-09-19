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
        services.TryAddSingleton(new BackstageAvailability(false));
        // Both default to withholding. A host that can establish backstage authority replaces them.
        services.TryAddScoped<IBackstageVisibility>(_ => OrdinaryVisibility.Instance);
        services.TryAddScoped<IBackstageAccount>(_ => NoBackstageAccount.Instance);
        services.AddSingleton<SqliteBackstageSessionStore>();
        // Every activation and deactivation goes through the cache so a read path can answer
        // synchronously without touching storage.
        services.AddSingleton(provider => new CachedBackstageSessions(
            provider.GetRequiredService<SqliteBackstageSessionStore>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IBackstageSessionStore>(provider => provider.GetRequiredService<CachedBackstageSessions>());
        services.AddScoped<IBackstageRecordStore, SqliteBackstageRecordStore>();
        services.AddScoped<IRecordTagStore, SqliteRecordTagStore>();
        services.AddScoped<ITagCatalogue, SqliteTagCatalogue>();
        services.AddSingleton<TagCatalogueSync>();
        services.AddSingleton<TagRenameDrainer>();
        services.AddSingleton<ITagMaintenance, TagMaintenance>();
        services.AddScoped<IBackstageService, BackstageService>();
        services.TryAddScoped<ICurrentDomainScope, DefaultCurrentDomain>();
        services.TryAddScoped<ICurrentDomain>(provider => provider.GetRequiredService<ICurrentDomainScope>());
        services.AddSingleton<DomainRegistryConnectionFactory>();
        services.AddSingleton<DomainMigrationTarget>();
        services.AddSingleton<MonkeysphereMigrationConnectionFactory>();
        services.AddSingleton<IDomainDatabaseMigrator, DomainDatabaseMigrator>();
        services.AddSingleton<DomainCatalog>();
        // The registry is the deployment's complete truth and stays a singleton; the catalogue is
        // the caller's filtered view and must be scoped, because backstage authority is per-caller.
        services.AddSingleton<IDomainRegistry>(provider => provider.GetRequiredService<DomainCatalog>());
        services.AddScoped<IDomainCatalog>(provider => new VisibleDomainCatalog(
            provider.GetRequiredService<DomainCatalog>(),
            provider.GetRequiredService<IBackstageVisibility>()));
        services.AddSingleton<IDomainCommands>(provider => provider.GetRequiredService<DomainCatalog>());
        services.AddSingleton<RemoteUploadConnections>();
        services.AddSingleton<IRemoteUploadStore, RemoteUploadStore>();
        services.AddSingleton<ContactUploadService>();
        services.AddSingleton<IContactImportPreviewStore, ContactImportPreviewStore>();
        services.AddScoped<ContactImportPreviewService>();
        services.AddScoped<IContactImportCommandStore, ContactImportCommandStore>();
        services.AddScoped<ContactImportCommandService>();
        services.AddScoped<MonkeysphereConnectionFactory>();
        services.AddSingleton<RecordMediaLocks>();
        services.AddScoped<IMonkeysphereStore, SqliteMonkeysphereStore>();
        services.AddScoped<IRecordCommandStore, SqliteMonkeysphereStore>();
        services.AddScoped<IMonkeysphereService, MonkeysphereService>();
        services.AddScoped<RecordCommandService>();
        services.AddScoped<IRecordBatchStore, SqliteMonkeysphereStore>();
        services.AddScoped<RecordBatchService>();
        services.AddScoped<IRecordDeletionStore, SqliteMonkeysphereStore>();
        services.AddScoped<RecordDeletionService>();
        services.AddScoped<IApplicationCommandAudit, SqliteApplicationCommandAudit>();
        services.AddScoped<ICalendarStore, SqliteCalendarStore>();
        services.AddScoped<ICalendarService, CalendarService>();
        services.AddScoped<ISpatialMapStore, SqliteSpatialMapStore>();
        services.AddScoped<ISpatialMapService, SpatialMapService>();
        services.AddScoped<IReminderStore, SqliteReminderStore>();
        services.AddScoped<IReminderService, ReminderService>();
        services.AddScoped<IVCardStore, SqliteVCardStore>();
        services.AddScoped<IRecordSourceStore, SqliteRecordSourceStore>();
        services.AddScoped<IRecordSourceService, RecordSourceService>();
        services.AddScoped<IVCardService, VCardService>();
        services.AddScoped<ContactPhotoImporter>();
        services.AddScoped<IContactSourceCardStore, SqliteContactSourceCardStore>();
        services.AddScoped<IContactEnrichmentBackfill, ContactEnrichmentBackfill>();
        services.AddScoped<IRecordImageService, RecordImageService>();
        services.AddScoped<IBackupService, BackupService>();
        services.AddScoped<IRelationshipStore, SqliteRelationshipStore>();
        services.AddScoped<IRelationshipService, RelationshipService>();
        services.AddScoped<IRelationshipCommandStore, SqliteMonkeysphereStore>();
        services.AddScoped<RelationshipCommandService>();
        services.AddScoped<IStructureCommandStore, SqliteMonkeysphereStore>();
        services.AddScoped<StructureCommandService>();
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
        services.AddScoped<IGraphSettingsStore, SqliteGraphSettingsStore>();
        services.AddScoped<IGraphSettingsService, GraphSettingsService>();
        services.AddScoped<IPresetStore, SqlitePresetStore>();
        services.AddScoped<IPresetService, PresetService>();
        services.AddScoped<IPresetCommandStore, SqliteMonkeysphereStore>();
        services.AddScoped<PresetCommandService>();
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
        services.AddDnaXDataMigrations(RemoteUploadSchema.DatabaseName, options =>
        {
            options.ConnectionFactory = provider => provider.GetRequiredService<RemoteUploadConnections>().CreateConnection();
            options.Manifest = RemoteUploadSchema.Manifest;
            options.ApplicationVersion = typeof(RemoteUploadSchema).Assembly.GetName().Version?.ToString();
            options.UseSqlite(sqlite =>
            {
                sqlite.EnableWriteAheadLogging = true;
                sqlite.EnforceForeignKeys = true;
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
        await services.MigrateDnaXDatabaseAsync(RemoteUploadSchema.DatabaseName, cancellationToken).ConfigureAwait(false);
        await services.GetRequiredService<IRemoteUploadStore>().CleanupAsync(cancellationToken).ConfigureAwait(false);
        // The registry, not the filtered catalogue: every domain's database must be migrated,
        // and a hidden domain left unmigrated would fail the moment someone entered backstage.
        IDomainRegistry registry = services.GetRequiredService<IDomainRegistry>();
        await registry.InitializeAsync(cancellationToken).ConfigureAwait(false);
        IDomainDatabaseMigrator databases = services.GetRequiredService<IDomainDatabaseMigrator>();
        foreach (MonkeysphereDomain domain in registry.All)
        {
            await databases.MigrateAsync(domain.Id, cancellationToken).ConfigureAwait(false);
        }

        // After every domain is migrated, because this reconciles across databases and a migration
        // can only see one. Tags stored before the catalogue existed are adopted here.
        await services.GetRequiredService<TagCatalogueSync>().RunAsync(cancellationToken).ConfigureAwait(false);

        // A rename interrupted by a restart finishes here rather than staying half-applied.
        await services.GetRequiredService<TagRenameDrainer>().DrainAsync(cancellationToken).ConfigureAwait(false);
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
            DELETE FROM RecordCommandReceipts;
            DELETE FROM ContactImportReceipts;
            DELETE FROM RecordBatchPreviews;
            DELETE FROM RecordDeletionPreviews;
            DELETE FROM RecordMediaCleanup;
            DELETE FROM ApplicationCommandAudit;
            DELETE FROM DashboardRecurringFields;
            DELETE FROM DashboardCategories;
            DELETE FROM DashboardSettings;
            DELETE FROM MapSettings;
            DELETE FROM GraphSettings;
            DELETE FROM GraphViews;
            DELETE FROM SavedViews;
            DELETE FROM Relationships;
            DELETE FROM RelationshipTypes;
            DELETE FROM RecordSourceValues;
            DELETE FROM RecordSourceImports;
            DELETE FROM Reminders;
            DELETE FROM RecordImages;
            DELETE FROM RecordAliases;
            DELETE FROM RecordTags;
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
