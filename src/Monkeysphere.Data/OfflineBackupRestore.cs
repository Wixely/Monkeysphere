using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Monkeysphere.Data;

public static class OfflineBackupRestore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<string> RestoreAsync(
        string packagePath,
        string dataRoot,
        CancellationToken cancellationToken = default)
    {
        string package = Path.GetFullPath(packagePath);
        string root = Directory.CreateDirectory(Path.GetFullPath(dataRoot)).FullName;
        if (!File.Exists(package))
        {
            throw new FileNotFoundException("The restore package was not found.", package);
        }

        Guid operationId = Guid.CreateVersion7();
        string staging = Directory.CreateDirectory(Path.Combine(root, "temporary", "restore-" + operationId.ToString("N"))).FullName;
        string rollback = Path.Combine(root, "backups", $"rollback-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{operationId:N}");
        try
        {
            await ExtractAndValidateAsync(package, staging, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(rollback);
            MoveCurrentToRollback(root, rollback);
            try
            {
                MoveStagedIntoPlace(staging, root);
            }
            catch
            {
                MoveCurrentToRollback(root, Path.Combine(rollback, "failed-restore"));
                MoveRollbackIntoPlace(rollback, root);
                throw;
            }

            return rollback;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static async Task ExtractAndValidateAsync(string package, string staging, CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(package);
        ZipArchiveEntry manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("The backup manifest is missing.");
        RestoreManifest manifest;
        await using (Stream stream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<RestoreManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The backup manifest is invalid.");
        }

        if (manifest.FormatVersion != 1 || manifest.ApplicationSchemaVersion is < 1 ||
            manifest.ApplicationSchemaVersion > MonkeysphereSchema.Manifest.CurrentVersion)
        {
            throw new InvalidDataException("The backup format or schema version is incompatible.");
        }

        Dictionary<string, RestoreEntry> expected = manifest.Entries.ToDictionary(entry => SafePath(entry.Path), StringComparer.Ordinal);
        ZipArchiveEntry[] payload = archive.Entries.Where(entry => entry.FullName != "manifest.json").ToArray();
        if (expected.Count != payload.Length || payload.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != payload.Length)
        {
            throw new InvalidDataException("The backup manifest does not match its payload.");
        }

        foreach (ZipArchiveEntry entry in payload)
        {
            string relative = SafePath(entry.FullName);
            if (!expected.Remove(relative, out RestoreEntry? specification) || entry.Length != specification.ByteLength)
            {
                throw new InvalidDataException($"Backup entry '{relative}' is missing or has the wrong length.");
            }

            string destination = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The backup contains an unsafe archive path.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using Stream source = entry.Open();
            await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131_072, true);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[131_072];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
            }

            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), specification.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Backup entry '{relative}' failed checksum validation.");
            }
        }

        string applicationDatabase = Path.Combine(staging, "databases", "monkeysphere.db");
        string remoteDatabase = Path.Combine(staging, "databases", "remote-access.db");
        await ValidateDatabaseAsync(applicationDatabase, cancellationToken).ConfigureAwait(false);
        await ValidateDatabaseAsync(remoteDatabase, cancellationToken).ConfigureAwait(false);
        await ValidateApplicationVersionAsync(applicationDatabase, manifest.ApplicationSchemaVersion, cancellationToken).ConfigureAwait(false);
        await ValidateMediaAsync(applicationDatabase, staging, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        string? result = await connection.ExecuteScalarAsync<string>(new CommandDefinition("PRAGMA integrity_check;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A restore database failed SQLite integrity validation.");
        }

        IEnumerable<dynamic> foreignKeys = await connection.QueryAsync(new CommandDefinition("PRAGMA foreign_key_check;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (foreignKeys.Any())
        {
            throw new InvalidDataException("A restore database failed SQLite foreign-key validation.");
        }
    }

    private static async Task ValidateApplicationVersionAsync(string path, int expectedVersion, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        int actual = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT coalesce(max(Version), 0) FROM __DnaXMigrations;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (actual != expectedVersion)
        {
            throw new InvalidDataException("The backup manifest does not match its DnaX migration ledger.");
        }
    }

    private static async Task ValidateMediaAsync(string database, string staging, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(database, cancellationToken).ConfigureAwait(false);
        IEnumerable<RestoreImageRow> images = await connection.QueryAsync<RestoreImageRow>(new CommandDefinition(
            "SELECT Id, RecordId, OriginalContentType FROM RecordImages;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        HashSet<string> expectedPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (RestoreImageRow image in images)
        {
            string extension = image.OriginalContentType switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/webp" => ".webp",
                _ => throw new InvalidDataException("The restore database references an unsupported original image type."),
            };
            string path = Path.Combine(staging, "media", "records", Guid.ParseExact(image.RecordId, "D").ToString("N"), Guid.ParseExact(image.Id, "D").ToString("N") + ".original" + extension);
            expectedPaths.Add(Path.GetFullPath(path));
            if (!File.Exists(path))
            {
                throw new InvalidDataException("The restore package is missing database-referenced original media.");
            }
        }

        string mediaRoot = Path.Combine(staging, "media", "records");
        IEnumerable<string> actualPaths = Directory.Exists(mediaRoot)
            ? Directory.EnumerateFiles(mediaRoot, "*.original.*", SearchOption.AllDirectories).Select(Path.GetFullPath)
            : [];
        if (!actualPaths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expectedPaths))
        {
            throw new InvalidDataException("The restore package contains original media not referenced by its database.");
        }
    }

    private static async Task<SqliteConnection> OpenAsync(string path, CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void MoveCurrentToRollback(string root, string rollback)
    {
        Directory.CreateDirectory(rollback);
        foreach (string name in new[] { "monkeysphere.db", "monkeysphere.db-wal", "monkeysphere.db-shm", "remote-access.db", "remote-access.db-wal", "remote-access.db-shm" })
        {
            string source = Path.Combine(root, name);
            if (File.Exists(source))
            {
                File.Move(source, Path.Combine(rollback, name));
            }
        }

        string media = Path.Combine(root, "media", "records");
        if (Directory.Exists(media))
        {
            Directory.CreateDirectory(Path.Combine(rollback, "media"));
            Directory.Move(media, Path.Combine(rollback, "media", "records"));
        }
    }

    private static void MoveStagedIntoPlace(string staging, string root)
    {
        File.Move(Path.Combine(staging, "databases", "monkeysphere.db"), Path.Combine(root, "monkeysphere.db"));
        File.Move(Path.Combine(staging, "databases", "remote-access.db"), Path.Combine(root, "remote-access.db"));
        string stagedMedia = Path.Combine(staging, "media", "records");
        if (Directory.Exists(stagedMedia))
        {
            Directory.CreateDirectory(Path.Combine(root, "media"));
            Directory.Move(stagedMedia, Path.Combine(root, "media", "records"));
        }
    }

    private static void MoveRollbackIntoPlace(string rollback, string root)
    {
        foreach (string source in Directory.EnumerateFiles(rollback, "*.db*", SearchOption.TopDirectoryOnly))
        {
            File.Move(source, Path.Combine(root, Path.GetFileName(source)), true);
        }

        string media = Path.Combine(rollback, "media", "records");
        if (Directory.Exists(media))
        {
            Directory.CreateDirectory(Path.Combine(root, "media"));
            Directory.Move(media, Path.Combine(root, "media", "records"));
        }
    }

    private static string SafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith('/') ||
            path.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("The backup contains an unsafe archive path.");
        }

        return path;
    }

    private sealed record RestoreManifest(int FormatVersion, Guid BackupId, int ApplicationSchemaVersion, IReadOnlyList<RestoreEntry> Entries);
    private sealed record RestoreEntry(string Path, string Kind, long ByteLength, string Sha256);
    private sealed class RestoreImageRow
    {
        public required string Id { get; init; }
        public required string RecordId { get; init; }
        public required string OriginalContentType { get; init; }
    }
}
