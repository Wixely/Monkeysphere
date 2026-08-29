using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed class SqliteCalendarStore(MonkeysphereConnectionFactory connections) : ICalendarStore
{
    public async Task<IReadOnlyList<CalendarEntry>> QueryAsync(
        CalendarQuery query,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<CalendarRow> rows = await connection.QueryAsync<CalendarRow>(new CommandDefinition("""
            SELECT r.Id AS RecordId,
                   r.RecordTypeId,
                   rt.Name AS RecordTypeName,
                   r.DisplayName AS RecordDisplayName,
                   fv.FieldDefinitionId,
                   fd.Name AS FieldName,
                   CASE fd.TypeId
                       WHEN 'exact-date' THEN fv.DateValue
                       ELSE fv.TemporalValue
                   END AS EventDate
            FROM FieldValues fv
            INNER JOIN Records r ON r.Id = fv.RecordId
            INNER JOIN RecordTypes rt ON rt.Id = r.RecordTypeId
            INNER JOIN FieldDefinitions fd ON fd.Id = fv.FieldDefinitionId
            WHERE ((fd.TypeId = 'exact-date' AND fv.DateValue IS NOT NULL)
                   OR (fd.TypeId = 'temporal'
                       AND fv.TemporalPrecision = @DayPrecision
                       AND fv.IsApproximate = 0
                       AND fv.TemporalValue IS NOT NULL))
              AND (CASE fd.TypeId WHEN 'exact-date' THEN fv.DateValue ELSE fv.TemporalValue END) BETWEEN @From AND @To
              AND (@RecordTypeId IS NULL OR r.RecordTypeId = @RecordTypeId)
              AND (@FieldDefinitionId IS NULL OR fv.FieldDefinitionId = @FieldDefinitionId)
            ORDER BY EventDate, r.DisplayName COLLATE NOCASE, fd.Name COLLATE NOCASE, r.Id, fv.Ordinal
            LIMIT @Limit;
            """,
            new
            {
                DayPrecision = (int)TemporalPrecision.Day,
                From = query.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                To = query.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                RecordTypeId = query.RecordTypeId?.ToString("D", CultureInfo.InvariantCulture),
                FieldDefinitionId = query.FieldDefinitionId?.ToString("D", CultureInfo.InvariantCulture),
                query.Limit,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(row => new CalendarEntry(
            Guid.ParseExact(row.RecordId, "D"),
            Guid.ParseExact(row.RecordTypeId, "D"),
            row.RecordTypeName,
            row.RecordDisplayName,
            Guid.ParseExact(row.FieldDefinitionId, "D"),
            row.FieldName,
            DateOnly.ParseExact(row.EventDate, "yyyy-MM-dd", CultureInfo.InvariantCulture))).ToArray();
    }

    private sealed class CalendarRow
    {
        public required string RecordId { get; init; }
        public required string RecordTypeId { get; init; }
        public required string RecordTypeName { get; init; }
        public required string RecordDisplayName { get; init; }
        public required string FieldDefinitionId { get; init; }
        public required string FieldName { get; init; }
        public required string EventDate { get; init; }
    }
}
