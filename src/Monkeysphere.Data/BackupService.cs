using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using DnaX.Hosting;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed class BackupService(
    MonkeysphereConnectionFactory connections,
    IDnaXPaths paths,
    TimeProvider timeProvider) : IBackupService
{
    private const int FormatVersion = 1;
    private const int MaximumEntries = 100_005;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<BackupInfo> CreateAsync(CancellationToken cancellationToken = default)
    {
        Guid id = Guid.CreateVersion7();
        DateTimeOffset createdAt = timeProvider.GetUtcNow();
        string backupDirectory = Directory.CreateDirectory(paths.ResolveWritable("backups")).FullName;
        string temporaryDirectory = Directory.CreateDirectory(
            paths.ResolveWritable(Path.Combine("temporary", "backup-" + id.ToString("N")))).FullName;
        string fileName = $"monkeysphere-{createdAt:yyyyMMdd-HHmmss}-{id:N}.monkeysphere-backup";
        string finalPath = Path.Combine(backupDirectory, fileName);
        string partialPath = finalPath + ".partial";

        try
        {
            string applicationSnapshot = Path.Combine(temporaryDirectory, "monkeysphere.db");
            string remoteSnapshot = Path.Combine(temporaryDirectory, "remote-access.db");
            await BackupApplicationDatabaseAsync(applicationSnapshot, cancellationToken).ConfigureAwait(false);
            await BackupDatabaseFileAsync(
                paths.ResolveWritable("remote-access.db"),
                remoteSnapshot,
                cancellationToken).ConfigureAwait(false);

            List<BackupManifestEntry> entries = [];
            await using (FileStream output = new(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131_072, true))
            using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                await AddFileAsync(archive, applicationSnapshot, "databases/monkeysphere.db", "application-database", entries, cancellationToken).ConfigureAwait(false);
                await AddFileAsync(archive, remoteSnapshot, "databases/remote-access.db", "remote-access-database", entries, cancellationToken).ConfigureAwait(false);

                foreach (ImageBackupRow image in await ListSnapshotImagesAsync(applicationSnapshot, cancellationToken).ConfigureAwait(false))
                {
                    string extension = image.OriginalContentType switch
                    {
                        "image/jpeg" => ".jpg",
                        "image/png" => ".png",
                        "image/webp" => ".webp",
                        _ => throw new InvalidDataException("The database references an unsupported original image type."),
                    };
                    string sourcePath = RecordImageStoragePaths.OriginalPath(
                        paths,
                        Guid.ParseExact(image.RecordId, "D"),
                        Guid.ParseExact(image.Id, "D"),
                        extension);
                    string archivePath = $"media/records/{Guid.ParseExact(image.RecordId, "D"):N}/{Guid.ParseExact(image.Id, "D"):N}.original{extension}";
                    await AddFileAsync(archive, sourcePath, archivePath, "image-original", entries, cancellationToken).ConfigureAwait(false);
                }

                BackupManifest manifest = new(
                    FormatVersion,
                    id,
                    createdAt,
                    typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "unknown",
                    MonkeysphereSchema.Manifest.CurrentVersion,
                    entries);
                ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using Stream manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(partialPath, finalPath);
            BackupInfo backup = Describe(finalPath);
            _ = await ValidateAsync(backup.Id, cancellationToken).ConfigureAwait(false);
            return backup;
        }
        catch
        {
            DeleteIfExists(partialPath);
            DeleteIfExists(finalPath);
            throw;
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    public Task<IReadOnlyList<BackupInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = Directory.CreateDirectory(paths.ResolveWritable("backups")).FullName;
        IReadOnlyList<BackupInfo> backups = Directory.EnumerateFiles(directory, "*.monkeysphere-backup", SearchOption.TopDirectoryOnly)
            .Select(Describe)
            .OrderByDescending(backup => backup.CreatedAtUtc)
            .ToArray();
        return Task.FromResult(backups);
    }

    public async Task<BackupValidation> ValidateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        string path = FindPath(id) ?? throw new FileNotFoundException("Backup was not found.");
        BackupInfo backup = Describe(path);
        string validationDirectory = Directory.CreateDirectory(
            paths.ResolveWritable(Path.Combine("temporary", "validate-" + Guid.NewGuid().ToString("N")))).FullName;
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count is < 3 or > MaximumEntries ||
                archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != archive.Entries.Count)
            {
                throw new InvalidDataException("The backup has an invalid entry count or duplicate paths.");
            }

            ZipArchiveEntry manifestEntry = archive.GetEntry("manifest.json")
                ?? throw new InvalidDataException("The backup manifest is missing.");
            BackupManifest manifest;
            await using (Stream manifestStream = manifestEntry.Open())
            {
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(manifestStream, JsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The backup manifest is invalid.");
            }

            if (manifest.FormatVersion != FormatVersion || manifest.BackupId != id ||
                manifest.ApplicationSchemaVersion is < 1 || manifest.ApplicationSchemaVersion > MonkeysphereSchema.Manifest.CurrentVersion)
            {
                throw new InvalidDataException("The backup format, identity, or schema version is incompatible.");
            }

            Dictionary<string, ZipArchiveEntry> payload = archive.Entries
                .Where(entry => entry.FullName != "manifest.json")
                .ToDictionary(entry => ValidateArchivePath(entry.FullName), StringComparer.Ordinal);
            if (manifest.Entries.Count != payload.Count || manifest.Entries.Count > MaximumEntries - 1)
            {
                throw new InvalidDataException("The backup manifest does not match the archive payload.");
            }

            foreach (BackupManifestEntry expected in manifest.Entries)
            {
                string entryPath = ValidateArchivePath(expected.Path);
                if (!payload.Remove(entryPath, out ZipArchiveEntry? entry) || entry.Length != expected.ByteLength)
                {
                    throw new InvalidDataException($"Backup entry '{entryPath}' is missing or has the wrong length.");
                }

                await using Stream content = entry.Open();
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(hash, expected.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Backup entry '{entryPath}' failed checksum validation.");
                }
            }

            if (payload.Count != 0)
            {
                throw new InvalidDataException("The backup contains unmanifested payload entries.");
            }

            string applicationDatabase = await ExtractForValidationAsync(
                archive,
                "databases/monkeysphere.db",
                validationDirectory,
                cancellationToken).ConfigureAwait(false);
            string remoteDatabase = await ExtractForValidationAsync(
                archive,
                "databases/remote-access.db",
                validationDirectory,
                cancellationToken).ConfigureAwait(false);
            await ValidateSqliteAsync(applicationDatabase, cancellationToken).ConfigureAwait(false);
            await ValidateSqliteAsync(remoteDatabase, cancellationToken).ConfigureAwait(false);
            await ValidateMediaReferencesAsync(applicationDatabase, manifest.Entries, cancellationToken).ConfigureAwait(false);

            return new(
                backup,
                manifest.FormatVersion,
                manifest.ApplicationSchemaVersion,
                manifest.Entries.Count,
                manifest.Entries.Count(entry => entry.Kind == "image-original"));
        }
        finally
        {
            if (Directory.Exists(validationDirectory))
            {
                Directory.Delete(validationDirectory, recursive: true);
            }
        }
    }

    public Task<Stream?> OpenAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? path = FindPath(id);
        Stream? stream = path is null
            ? null
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131_072, true);
        return Task.FromResult(stream);
    }

    public async Task PruneAsync(int retentionCount, CancellationToken cancellationToken = default)
    {
        if (retentionCount is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionCount), "Backup retention must be between 1 and 1,000 packages.");
        }

        IReadOnlyList<BackupInfo> backups = await ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (BackupInfo backup in backups.Skip(retentionCount))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? path = FindPath(backup.Id);
            if (path is not null)
            {
                File.Delete(path);
            }
        }
    }

    private async Task BackupApplicationDatabaseAsync(string destination, CancellationToken cancellationToken)
    {
        await using SqliteConnection source = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await BackupOpenConnectionAsync(source, destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task BackupDatabaseFileAsync(string sourcePath, string destination, CancellationToken cancellationToken)
    {
        SqliteConnectionStringBuilder sourceBuilder = new()
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        await using SqliteConnection source = new(sourceBuilder.ConnectionString);
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await BackupOpenConnectionAsync(source, destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task BackupOpenConnectionAsync(SqliteConnection source, string destination, CancellationToken cancellationToken)
    {
        SqliteConnectionStringBuilder destinationBuilder = new()
        {
            DataSource = destination,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        await using SqliteConnection target = new(destinationBuilder.ConnectionString);
        await target.OpenAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(target);
        await ValidateSqliteAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddFileAsync(
        ZipArchive archive,
        string sourcePath,
        string archivePath,
        string kind,
        List<BackupManifestEntry> entries,
        CancellationToken cancellationToken)
    {
        FileInfo file = new(sourcePath);
        if (!file.Exists)
        {
            throw new InvalidDataException($"Required backup source '{archivePath}' is missing.");
        }

        string hash;
        await using (FileStream hashStream = file.OpenRead())
        {
            hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken).ConfigureAwait(false));
        }

        ZipArchiveEntry entry = archive.CreateEntry(archivePath, CompressionLevel.Optimal);
        await using Stream destination = entry.Open();
        await using FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131_072, true);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        entries.Add(new(archivePath, kind, file.Length, hash));
    }

    private static async Task<IReadOnlyList<ImageBackupRow>> ListSnapshotImagesAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenReadOnlyAsync(databasePath, cancellationToken).ConfigureAwait(false);
        IEnumerable<ImageBackupRow> rows = await connection.QueryAsync<ImageBackupRow>(new CommandDefinition("""
            SELECT Id, RecordId, OriginalContentType
            FROM RecordImages
            ORDER BY RecordId, Ordinal, Id;
            """, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToArray();
    }

    private static async Task ValidateMediaReferencesAsync(
        string databasePath,
        IReadOnlyList<BackupManifestEntry> entries,
        CancellationToken cancellationToken)
    {
        HashSet<string> originals = entries
            .Where(entry => entry.Kind == "image-original")
            .Select(entry => entry.Path)
            .ToHashSet(StringComparer.Ordinal);
        foreach (ImageBackupRow image in await ListSnapshotImagesAsync(databasePath, cancellationToken).ConfigureAwait(false))
        {
            string extension = image.OriginalContentType switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/webp" => ".webp",
                _ => throw new InvalidDataException("The backup database references an unsupported original image type."),
            };
            string expected = $"media/records/{Guid.ParseExact(image.RecordId, "D"):N}/{Guid.ParseExact(image.Id, "D"):N}.original{extension}";
            if (!originals.Remove(expected))
            {
                throw new InvalidDataException($"The backup is missing original media '{expected}'.");
            }
        }

        if (originals.Count != 0)
        {
            throw new InvalidDataException("The backup contains original media not referenced by its database snapshot.");
        }
    }

    private static async Task ValidateSqliteAsync(string path, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenReadOnlyAsync(path, cancellationToken).ConfigureAwait(false);
        string? integrity = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "PRAGMA integrity_check;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A backup database failed SQLite integrity validation.");
        }

        IEnumerable<string> foreignKeyIssues = await connection.QueryAsync<string>(new CommandDefinition(
            "PRAGMA foreign_key_check;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (foreignKeyIssues.Any())
        {
            throw new InvalidDataException("A backup database failed SQLite foreign-key validation.");
        }
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(string path, CancellationToken cancellationToken)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        SqliteConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<string> ExtractForValidationAsync(
        ZipArchive archive,
        string entryPath,
        string directory,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.GetEntry(entryPath)
            ?? throw new InvalidDataException($"Backup entry '{entryPath}' is missing.");
        string destination = Path.Combine(directory, Path.GetFileName(entryPath));
        await using Stream source = entry.Open();
        await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131_072, true);
        await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return destination;
    }

    private string? FindPath(Guid id)
    {
        string directory = Directory.CreateDirectory(paths.ResolveWritable("backups")).FullName;
        string suffix = $"-{id:N}.monkeysphere-backup";
        return Directory.EnumerateFiles(directory, "*.monkeysphere-backup", SearchOption.TopDirectoryOnly)
            .SingleOrDefault(path => Path.GetFileName(path).EndsWith(suffix, StringComparison.Ordinal));
    }

    private static BackupInfo Describe(string path)
    {
        FileInfo file = new(path);
        string idText = Path.GetFileNameWithoutExtension(file.Name).Split('-').Last();
        Guid id = Guid.ParseExact(idText, "N");
        DateTimeOffset createdAt = file.CreationTimeUtc;
        return new(id, file.Name, createdAt, file.Length);
    }

    private static string ValidateArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\', StringComparison.Ordinal) ||
            path.StartsWith('/') || path.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("The backup contains an unsafe archive path.");
        }

        return path;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record BackupManifest(
        int FormatVersion,
        Guid BackupId,
        DateTimeOffset CreatedAtUtc,
        string ApplicationVersion,
        int ApplicationSchemaVersion,
        IReadOnlyList<BackupManifestEntry> Entries);

    private sealed record BackupManifestEntry(string Path, string Kind, long ByteLength, string Sha256);

    private sealed class ImageBackupRow
    {
        public required string Id { get; init; }
        public required string RecordId { get; init; }
        public required string OriginalContentType { get; init; }
    }
}
