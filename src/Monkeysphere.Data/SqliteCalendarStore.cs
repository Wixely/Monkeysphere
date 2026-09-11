using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed class SqliteCalendarStore(MonkeysphereConnectionFactory connections, IBackstageVisibility visibility) : ICalendarStore
{
    private string Visible(string alias) => BackstageFilter.AndVisible(visibility, alias);

    public async Task<IReadOnlyList<CalendarEntry>> QueryAsync(
        CalendarQuery query,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<CalendarRow> rows = await connection.QueryAsync<CalendarRow>(new CommandDefinition($"""
            SELECT fv.Id AS FieldValueId,
                   r.Id AS RecordId,
                   r.RecordTypeId,
                   rt.Name AS RecordTypeName,
                   r.DisplayName AS RecordDisplayName,
                   fv.FieldDefinitionId,
                   fd.Name AS FieldName,
                   rt.Symbol AS RecordTypeSymbol,
                   (SELECT image.Id
                    FROM RecordImages image
                    WHERE image.RecordId = r.Id
                    ORDER BY image.IsCover DESC, image.Ordinal, image.Id
                    LIMIT 1) AS ImageId,
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
              AND (@TypeCount = 0 OR r.RecordTypeId IN @RecordTypeIds)
              AND (@FieldDefinitionId IS NULL OR fv.FieldDefinitionId = @FieldDefinitionId){Visible("r")}
            ORDER BY EventDate, r.DisplayName COLLATE NOCASE, fd.Name COLLATE NOCASE, r.Id, fv.Ordinal
            LIMIT @Limit;
            """,
            new
            {
                DayPrecision = (int)TemporalPrecision.Day,
                From = query.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                To = query.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                RecordTypeId = query.RecordTypeId?.ToString("D", CultureInfo.InvariantCulture),
                TypeCount = query.RecordTypeIds.Count,
                RecordTypeIds = TypeIds(query),
                FieldDefinitionId = query.FieldDefinitionId?.ToString("D", CultureInfo.InvariantCulture),
                query.Limit,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(Map).ToArray();
    }

    /// <summary>
    /// Every day-precision value of the named fields, whatever year it falls in. The range is not
    /// applied here on purpose: a birthday from 1990 has to be readable in order to be projected
    /// into the month being looked at.
    /// </summary>
    public async Task<IReadOnlyList<CalendarEntry>> ListRepeatingValuesAsync(
        IReadOnlyList<Guid> fieldDefinitionIds,
        CalendarQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fieldDefinitionIds);
        ArgumentNullException.ThrowIfNull(query);
        if (fieldDefinitionIds.Count == 0) return [];

        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<CalendarRow> rows = await connection.QueryAsync<CalendarRow>(new CommandDefinition($"""
            SELECT fv.Id AS FieldValueId,
                   r.Id AS RecordId,
                   r.RecordTypeId,
                   rt.Name AS RecordTypeName,
                   r.DisplayName AS RecordDisplayName,
                   fv.FieldDefinitionId,
                   fd.Name AS FieldName,
                   rt.Symbol AS RecordTypeSymbol,
                   (SELECT image.Id
                    FROM RecordImages image
                    WHERE image.RecordId = r.Id
                    ORDER BY image.IsCover DESC, image.Ordinal, image.Id
                    LIMIT 1) AS ImageId,
                   CASE fd.TypeId
                       WHEN 'exact-date' THEN fv.DateValue
                       ELSE fv.TemporalValue
                   END AS EventDate
            FROM FieldValues fv
            INNER JOIN Records r ON r.Id = fv.RecordId
            INNER JOIN RecordTypes rt ON rt.Id = r.RecordTypeId
            INNER JOIN FieldDefinitions fd ON fd.Id = fv.FieldDefinitionId
            WHERE fv.FieldDefinitionId IN @FieldDefinitionIds
              AND ((fd.TypeId = 'exact-date' AND fv.DateValue IS NOT NULL)
                   OR (fd.TypeId = 'temporal'
                       AND fv.TemporalPrecision = @DayPrecision
                       AND fv.IsApproximate = 0
                       AND fv.TemporalValue IS NOT NULL))
              AND (@RecordTypeId IS NULL OR r.RecordTypeId = @RecordTypeId)
              AND (@TypeCount = 0 OR r.RecordTypeId IN @RecordTypeIds){Visible("r")}
            ORDER BY r.DisplayName COLLATE NOCASE, fd.Name COLLATE NOCASE, r.Id, fv.Ordinal
            LIMIT @Limit;
            """,
            new
            {
                FieldDefinitionIds = fieldDefinitionIds.Select(id => id.ToString("D", CultureInfo.InvariantCulture)).ToArray(),
                DayPrecision = (int)TemporalPrecision.Day,
                RecordTypeId = query.RecordTypeId?.ToString("D", CultureInfo.InvariantCulture),
                TypeCount = query.RecordTypeIds.Count,
                RecordTypeIds = TypeIds(query),
                Limit = MaximumRepeatingValues,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(Map).ToArray();
    }

    /// <summary>
    /// How many repeating values one range may project from. Generous enough for a personal address
    /// book and bounded so that a very large one cannot read every dated value into memory at once.
    /// </summary>
    private const int MaximumRepeatingValues = 5_000;

    /// <summary>Dapper needs a non-empty list even when the count says it is unused.</summary>
    private static string[] TypeIds(CalendarQuery query) => query.RecordTypeIds.Count == 0
        ? [Guid.Empty.ToString("D", CultureInfo.InvariantCulture)]
        : query.RecordTypeIds.Select(id => id.ToString("D", CultureInfo.InvariantCulture)).ToArray();

    private static CalendarEntry Map(CalendarRow row) => new(
        Guid.ParseExact(row.FieldValueId, "D"),
        Guid.ParseExact(row.RecordId, "D"),
        Guid.ParseExact(row.RecordTypeId, "D"),
        row.RecordTypeName,
        row.RecordDisplayName,
        Guid.ParseExact(row.FieldDefinitionId, "D"),
        row.FieldName,
        DateOnly.ParseExact(row.EventDate, "yyyy-MM-dd", CultureInfo.InvariantCulture))
    {
        ImageId = row.ImageId is null ? null : Guid.ParseExact(row.ImageId, "D"),
        RecordTypeSymbol = row.RecordTypeSymbol,
    };

    private sealed class CalendarRow
    {
        public required string FieldValueId { get; init; }
        public required string RecordId { get; init; }
        public required string RecordTypeId { get; init; }
        public required string RecordTypeName { get; init; }
        public required string RecordDisplayName { get; init; }
        public required string FieldDefinitionId { get; init; }
        public required string FieldName { get; init; }
        public string? RecordTypeSymbol { get; init; }
        public string? ImageId { get; init; }
        public required string EventDate { get; init; }
    }
}
