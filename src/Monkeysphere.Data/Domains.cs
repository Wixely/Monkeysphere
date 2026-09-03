using System.Collections.Immutable;
using System.Globalization;
using System.Threading;
using Dapper;
using DnaX.Data.Migrations;
using DnaX.Hosting;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public static class DomainRegistrySchema
{
    public const string DatabaseName = "MonkeysphereDomains";

    public static DnaXMigrationManifest Manifest { get; } = new(
        currentVersion: 1,
        migrations:
        [
            DnaXMigration.Sql(1, "domain-registry", "Create the Monkeysphere domain registry", """
                CREATE TABLE Domains (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    IsDefault INTEGER NOT NULL CHECK (IsDefault IN (0, 1)),
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );

                CREATE UNIQUE INDEX IX_Domains_Default
                    ON Domains(IsDefault) WHERE IsDefault = 1;
                """),
        ]);
}

internal static class DomainStoragePaths
{
    internal static string DatabaseRelativePath(Guid domainId) =>
        domainId == MonkeysphereDomains.DefaultId
            ? "monkeysphere.db"
            : Path.Combine("domains", domainId.ToString("N"), "monkeysphere.db");

    internal static string MediaRelativeRoot(Guid domainId) =>
        domainId == MonkeysphereDomains.DefaultId
            ? Path.Combine("media", "records")
            : Path.Combine("domains", domainId.ToString("N"), "media", "records");
}

internal sealed class DomainRegistryConnectionFactory(IDnaXPaths paths)
{
    internal SqliteConnection CreateConnection() => Create(paths.ResolveWritable("domains.db"));

    internal async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    internal static SqliteConnection Create(string databasePath)
    {
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
}

internal sealed class DomainMigrationTarget
{
    private readonly AsyncLocal<string?> _path = new();

    internal string? Path
    {
        get => _path.Value;
        set => _path.Value = value;
    }
}

internal sealed class MonkeysphereMigrationConnectionFactory(
    IDnaXPaths paths,
    DomainMigrationTarget target)
{
    internal SqliteConnection CreateConnection()
    {
        string path = target.Path ?? paths.ResolveWritable(DomainStoragePaths.DatabaseRelativePath(MonkeysphereDomains.DefaultId));
        return DomainRegistryConnectionFactory.Create(path);
    }
}

internal interface IDomainDatabaseMigrator
{
    Task MigrateAsync(Guid domainId, CancellationToken cancellationToken = default);
}

internal sealed class DomainDatabaseMigrator(
    IDnaXPaths paths,
    DomainMigrationTarget target,
    IDnaXDatabaseMigrator migrator) : IDomainDatabaseMigrator, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task MigrateAsync(Guid domainId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            target.Path = paths.ResolveWritable(DomainStoragePaths.DatabaseRelativePath(domainId));
            await migrator.MigrateAsync(MonkeysphereDataExtensions.DatabaseName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            target.Path = null;
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

internal sealed class DefaultCurrentDomain : ICurrentDomainScope
{
    public Guid Id => MonkeysphereDomains.DefaultId;

    public IDisposable Use(Guid domainId)
    {
        if (domainId != Id)
        {
            throw new DomainValidationException("The requested domain is unavailable in this context.");
        }

        return EmptyScope.Instance;
    }

    private sealed class EmptyScope : IDisposable
    {
        internal static EmptyScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

internal sealed class DomainCatalog(
    DomainRegistryConnectionFactory connections,
    IDomainDatabaseMigrator domainDatabases,
    IDnaXPaths paths,
    TimeProvider timeProvider) : IDomainCatalog, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImmutableArray<MonkeysphereDomain> _domains = [];

    public IReadOnlyList<MonkeysphereDomain> Snapshot => _domains;

    public MonkeysphereDomain DefaultDomain => _domains.FirstOrDefault(domain => domain.IsDefault)
        ?? throw new InvalidOperationException("The default domain has not been initialized.");

    public bool TryGet(Guid id, out MonkeysphereDomain? domain)
    {
        domain = _domains.FirstOrDefault(item => item.Id == id);
        return domain is not null;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            string now = Timestamp(timeProvider.GetUtcNow());
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT OR IGNORE INTO Domains (Id, Name, IsDefault, CreatedAtUtc, UpdatedAtUtc)
                VALUES (@Id, 'Default', 1, @Now, @Now);
                """, new { Id = Key(MonkeysphereDomains.DefaultId), Now = now }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await RefreshAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MonkeysphereDomain> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        string normalized = MonkeysphereDomains.NormalizeName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Guid id = Guid.CreateVersion7();
        bool registered = false;
        string domainDirectory = paths.ResolveWritable(Path.Combine("domains", id.ToString("N")));
        try
        {
            if (_domains.Any(domain => string.Equals(domain.Name, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DomainValidationException("A domain with that name already exists.");
            }

            await domainDatabases.MigrateAsync(id, cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = timeProvider.GetUtcNow();
            await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO Domains (Id, Name, IsDefault, CreatedAtUtc, UpdatedAtUtc)
                VALUES (@Id, @Name, 0, @Now, @Now);
                """, new { Id = Key(id), Name = normalized, Now = Timestamp(now) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            registered = true;
            await RefreshAsync(connection, cancellationToken).ConfigureAwait(false);
            return _domains.Single(domain => domain.Id == id);
        }
        catch
        {
            if (!registered && Directory.Exists(domainDirectory))
            {
                Directory.Delete(domainDirectory, recursive: true);
            }

            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MonkeysphereDomain> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        string normalized = MonkeysphereDomains.NormalizeName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                int changed = await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE Domains SET Name = @Name, UpdatedAtUtc = @Now WHERE Id = @Id;
                    """, new { Id = Key(id), Name = normalized, Now = Timestamp(timeProvider.GetUtcNow()) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
                if (changed != 1)
                {
                    throw new DomainValidationException("Domain was not found.");
                }
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw new DomainValidationException("A domain with that name already exists.", exception);
            }

            await RefreshAsync(connection, cancellationToken).ConfigureAwait(false);
            return _domains.Single(domain => domain.Id == id);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        IEnumerable<DomainRow> rows = await connection.QueryAsync<DomainRow>(new CommandDefinition("""
            SELECT Id, Name, IsDefault, CreatedAtUtc, UpdatedAtUtc
            FROM Domains
            ORDER BY IsDefault DESC, Name COLLATE NOCASE, Id;
            """, cancellationToken: cancellationToken)).ConfigureAwait(false);
        _domains = rows.Select(row => new MonkeysphereDomain(
            Guid.ParseExact(row.Id, "D"),
            row.Name,
            row.IsDefault != 0,
            ParseTimestamp(row.CreatedAtUtc),
            ParseTimestamp(row.UpdatedAtUtc))).ToImmutableArray();
    }

    private static string Key(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class DomainRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public int IsDefault { get; init; }
        public required string CreatedAtUtc { get; init; }
        public required string UpdatedAtUtc { get; init; }
    }

    public void Dispose() => _gate.Dispose();
}
