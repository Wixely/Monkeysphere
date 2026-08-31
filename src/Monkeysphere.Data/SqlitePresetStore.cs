using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed class SqlitePresetStore(MonkeysphereConnectionFactory connections, TimeProvider timeProvider) : IPresetStore
{
    public async Task<SetupStatus> GetSetupStatusAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SetupRow? row = await connection.QuerySingleOrDefaultAsync<SetupRow>(new CommandDefinition(
            "SELECT StarterPackKey, CompletedAtUtc FROM SetupState WHERE Singleton = 1;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is not null)
        {
            return new(true, row.StarterPackKey, ParseTimestamp(row.CompletedAtUtc));
        }

        int existingTypes = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM RecordTypes;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (existingTypes == 0)
        {
            return new(false, null, null);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT OR IGNORE INTO SetupState (Singleton, StarterPackKey, CompletedAtUtc)
            VALUES (1, 'existing', @Now);
            """, new { Now = Timestamp(now) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new(true, "existing", now);
    }

    public async Task<IReadOnlySet<string>> ListInstalledPresetKeysAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<string> keys = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT PresetKey FROM RecordTypes WHERE PresetKey IS NOT NULL ORDER BY PresetKey;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task InstallAsync(PresetInstallation installation, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string now = Timestamp(installation.InstalledAtUtc);
        try
        {
            if (installation.StarterPackKey is not null)
            {
                int completed = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(*) FROM SetupState WHERE Singleton = 1;", transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                if (completed != 0)
                {
                    throw new DomainValidationException("First-run setup has already been completed.");
                }
            }

            foreach (RecordTypePresetInstallation type in installation.RecordTypes)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO RecordTypes (Id, Name, Symbol, CreatedAtUtc, UpdatedAtUtc, PresetKey, PresetVersion)
                    VALUES (@Id, @Name, @Symbol, @Now, @Now, @PresetKey, @PresetVersion);
                    """, new
                {
                    Id = Key(type.Id),
                    type.Preset.Name,
                    type.Preset.Symbol,
                    Now = now,
                    PresetKey = type.Preset.Key,
                    PresetVersion = type.Preset.Version,
                }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

                for (int order = 0; order < type.Fields.Count; order++)
                {
                    PresetFieldInstallation field = type.Fields[order];
                    await connection.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO FieldDefinitions
                            (Id, Name, TypeId, ConfigurationJson, Lifecycle, CreatedAtUtc, UpdatedAtUtc,
                             CanonicalKey, PresetKey, PresetVersion)
                        VALUES
                            (@Id, @Name, @TypeId, @ConfigurationJson, 0, @Now, @Now,
                             @CanonicalKey, @PresetKey, @PresetVersion);
                        INSERT INTO RecordTypeFields (RecordTypeId, FieldDefinitionId, SortOrder, IsRequired)
                        VALUES (@RecordTypeId, @Id, @SortOrder, @IsRequired);
                        """, new
                    {
                        Id = Key(field.Id),
                        field.Definition.Name,
                        field.Definition.TypeId,
                        field.ConfigurationJson,
                        Now = now,
                        field.Definition.CanonicalKey,
                        PresetKey = type.Preset.Key,
                        PresetVersion = type.Preset.Version,
                        RecordTypeId = Key(type.Id),
                        SortOrder = order,
                        IsRequired = field.Definition.IsRequired ? 1 : 0,
                    }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                }
            }

            foreach (RelationshipTypePresetInstallation relationship in installation.RelationshipTypes)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO RelationshipTypes
                        (Id, Name, Directionality, InverseName, Lifecycle, CreatedAtUtc, UpdatedAtUtc, PresetKey, PresetVersion)
                    VALUES (@Id, @Name, 0, @InverseName, 0, @Now, @Now, @PresetKey, @PresetVersion);
                    """, new
                {
                    Id = Key(relationship.Id),
                    relationship.Preset.Name,
                    relationship.Preset.InverseName,
                    Now = now,
                    PresetKey = relationship.Preset.Key,
                    PresetVersion = relationship.Preset.Version,
                }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            if (installation.StarterPackKey is not null)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO SetupState (Singleton, StarterPackKey, CompletedAtUtc)
                    VALUES (1, @StarterPackKey, @Now);
                    """, new { installation.StarterPackKey, Now = now }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw new DomainValidationException(
                "The preset could not be installed because one of its names or identities already exists.", exception);
        }
    }

    private static string Key(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class SetupRow
    {
        public required string StarterPackKey { get; init; }
        public required string CompletedAtUtc { get; init; }
    }
}
