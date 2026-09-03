using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed class SqliteDashboardStore(MonkeysphereConnectionFactory connections) : IDashboardStore
{
    public async Task<DashboardConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        DashboardConfigurationRow? row = await connection.QuerySingleOrDefaultAsync<DashboardConfigurationRow>(new CommandDefinition("""
            SELECT RecordTypeId, UpcomingDays
            FROM DashboardSettings
            WHERE Singleton = 1;
            """, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        string[] fields = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT FieldDefinitionId
            FROM DashboardRecurringFields
            ORDER BY SortOrder, FieldDefinitionId;
            """, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
        return new(
            ParseNullableGuid(row.RecordTypeId),
            fields.Select(ParseGuid).ToArray(),
            row.UpcomingDays);
    }

    public async Task SaveConfigurationAsync(
        DashboardConfiguration configuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO DashboardSettings (Singleton, RecordTypeId, UpcomingDays, UpdatedAtUtc)
                VALUES (1, @RecordTypeId, @UpcomingDays, @UpdatedAtUtc)
                ON CONFLICT (Singleton) DO UPDATE SET
                    RecordTypeId = excluded.RecordTypeId,
                    UpcomingDays = excluded.UpcomingDays,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;

                DELETE FROM DashboardRecurringFields;
                """, new
            {
                RecordTypeId = configuration.RecordTypeId is Guid typeId ? Key(typeId) : null,
                configuration.UpcomingDays,
                UpdatedAtUtc = Timestamp(now),
            }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            for (int index = 0; index < configuration.RecurringFieldDefinitionIds.Count; index++)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO DashboardRecurringFields (FieldDefinitionId, SortOrder)
                    VALUES (@FieldDefinitionId, @SortOrder);
                    """, new
                {
                    FieldDefinitionId = Key(configuration.RecurringFieldDefinitionIds[index]),
                    SortOrder = index,
                }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new DomainValidationException("Dashboard settings refer to a record type or field that is no longer available.", exception);
        }
    }

    public async Task<IReadOnlyList<DashboardDateSource>> ListDateSourcesAsync(
        IReadOnlyList<Guid> fieldDefinitionIds,
        CancellationToken cancellationToken = default)
    {
        if (fieldDefinitionIds.Count == 0)
        {
            return [];
        }

        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<DashboardDateRow> rows = await connection.QueryAsync<DashboardDateRow>(new CommandDefinition("""
            SELECT fv.Id AS FieldValueId,
                   r.Id AS RecordId,
                   r.RecordTypeId,
                   rt.Name AS RecordTypeName,
                   r.DisplayName AS RecordDisplayName,
                   fv.FieldDefinitionId,
                   fd.Name AS FieldName,
                   CASE fd.TypeId WHEN 'exact-date' THEN fv.DateValue ELSE fv.TemporalValue END AS EventValue,
                   CASE fd.TypeId WHEN 'exact-date' THEN @DayPrecision ELSE fv.TemporalPrecision END AS EventPrecision
            FROM FieldValues fv
            INNER JOIN Records r ON r.Id = fv.RecordId
            INNER JOIN RecordTypes rt ON rt.Id = r.RecordTypeId
            INNER JOIN FieldDefinitions fd ON fd.Id = fv.FieldDefinitionId
            WHERE fv.FieldDefinitionId IN @FieldDefinitionIds
              AND fd.Lifecycle = 0
              AND ((fd.TypeId = 'exact-date' AND fv.DateValue IS NOT NULL)
                   OR (fd.TypeId = 'temporal'
                       AND fv.TemporalPrecision BETWEEN @DayPrecision AND @SecondPrecision
                       AND fv.IsApproximate = 0
                       AND fv.TemporalValue IS NOT NULL));
            """, new
        {
            FieldDefinitionIds = fieldDefinitionIds.Select(Key).ToArray(),
            DayPrecision = (int)TemporalPrecision.Day,
            SecondPrecision = (int)TemporalPrecision.Second,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(row => new DashboardDateSource(
            ParseGuid(row.FieldValueId),
            ParseGuid(row.RecordId),
            ParseGuid(row.RecordTypeId),
            row.RecordTypeName,
            row.RecordDisplayName,
            ParseGuid(row.FieldDefinitionId),
            row.FieldName,
            row.EventValue,
            (TemporalPrecision)row.EventPrecision)).ToArray();
    }

    private static string Key(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);
    private static Guid ParseGuid(string value) => Guid.ParseExact(value, "D");
    private static Guid? ParseNullableGuid(string? value) => value is null ? null : ParseGuid(value);
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed class DashboardConfigurationRow
    {
        public string? RecordTypeId { get; init; }
        public int UpcomingDays { get; init; }
    }

    private sealed class DashboardDateRow
    {
        public required string FieldValueId { get; init; }
        public required string RecordId { get; init; }
        public required string RecordTypeId { get; init; }
        public required string RecordTypeName { get; init; }
        public required string RecordDisplayName { get; init; }
        public required string FieldDefinitionId { get; init; }
        public required string FieldName { get; init; }
        public required string EventValue { get; init; }
        public int EventPrecision { get; init; }
    }
}
