using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed class SqliteReminderStore(MonkeysphereConnectionFactory connections) : IReminderStore
{
    public async Task<Reminder> CreateAsync(
        Guid id,
        Guid fieldValueId,
        int leadDays,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int changed = await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO Reminders
                    (Id, RecordId, FieldDefinitionId, ValueOrdinal, LeadDays, CreatedAtUtc, DismissedAtUtc)
                SELECT @Id, fv.RecordId, fv.FieldDefinitionId, fv.Ordinal, @LeadDays, @CreatedAtUtc, NULL
                FROM FieldValues fv
                INNER JOIN FieldDefinitions fd ON fd.Id = fv.FieldDefinitionId
                WHERE fv.Id = @FieldValueId
                  AND ((fd.TypeId = 'exact-date' AND fv.DateValue IS NOT NULL)
                       OR (fd.TypeId = 'temporal'
                           AND fv.TemporalPrecision = @DayPrecision
                           AND fv.IsApproximate = 0
                           AND fv.TemporalValue IS NOT NULL));
                """, new
            {
                Id = Key(id),
                FieldValueId = Key(fieldValueId),
                LeadDays = leadDays,
                CreatedAtUtc = Timestamp(now),
                DayPrecision = (int)TemporalPrecision.Day,
            }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (changed != 1)
            {
                throw new DomainValidationException("The selected value is not eligible for an exact-date reminder.");
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new DomainValidationException("That reminder is already scheduled.", exception);
        }

        ReminderRow created = await connection.QuerySingleAsync<ReminderRow>(new CommandDefinition("""
            SELECT Id AS ReminderId, RecordId, FieldDefinitionId, ValueOrdinal, LeadDays, CreatedAtUtc
            FROM Reminders WHERE Id = @Id;
            """, new { Id = Key(id) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return MapReminder(created);
    }

    public async Task<IReadOnlyList<ReminderItem>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<ReminderRow> rows = await connection.QueryAsync<ReminderRow>(new CommandDefinition("""
            SELECT m.Id AS ReminderId, fv.Id AS FieldValueId, m.ValueOrdinal, m.LeadDays, m.CreatedAtUtc,
                   m.RecordId, r.RecordTypeId, rt.Name AS RecordTypeName,
                   r.DisplayName AS RecordDisplayName, fv.FieldDefinitionId, fd.Name AS FieldName,
                   CASE fd.TypeId WHEN 'exact-date' THEN fv.DateValue ELSE fv.TemporalValue END AS EventDate
            FROM Reminders m
            INNER JOIN FieldValues fv
                ON fv.RecordId = m.RecordId
               AND fv.FieldDefinitionId = m.FieldDefinitionId
               AND fv.Ordinal = m.ValueOrdinal
            INNER JOIN FieldDefinitions fd ON fd.Id = fv.FieldDefinitionId
            INNER JOIN Records r ON r.Id = fv.RecordId
            INNER JOIN RecordTypes rt ON rt.Id = r.RecordTypeId
            WHERE m.DismissedAtUtc IS NULL
              AND ((fd.TypeId = 'exact-date' AND fv.DateValue IS NOT NULL)
                   OR (fd.TypeId = 'temporal'
                       AND fv.TemporalPrecision = @DayPrecision
                       AND fv.IsApproximate = 0
                       AND fv.TemporalValue IS NOT NULL))
            ORDER BY EventDate, m.LeadDays DESC, r.DisplayName COLLATE NOCASE, m.Id;
            """, new { DayPrecision = (int)TemporalPrecision.Day }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(Map).ToArray();
    }

    public async Task<bool> DismissAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int changed = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE Reminders SET DismissedAtUtc = @Now
            WHERE Id = @Id AND DismissedAtUtc IS NULL;
            """, new { Id = Key(id), Now = Timestamp(now) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed == 1;
    }

    private static ReminderItem Map(ReminderRow row)
    {
        DateOnly eventDate = DateOnly.ParseExact(row.EventDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        Reminder reminder = MapReminder(row);
        CalendarEntry entry = new(
            Guid.ParseExact(row.FieldValueId, "D"),
            Guid.ParseExact(row.RecordId, "D"),
            Guid.ParseExact(row.RecordTypeId, "D"),
            row.RecordTypeName,
            row.RecordDisplayName,
            Guid.ParseExact(row.FieldDefinitionId, "D"),
            row.FieldName,
            eventDate);
        return new(reminder, entry, eventDate.AddDays(-reminder.LeadDays));
    }

    private static Reminder MapReminder(ReminderRow row) => new(
        Guid.ParseExact(row.ReminderId, "D"),
        Guid.ParseExact(row.RecordId, "D"),
        Guid.ParseExact(row.FieldDefinitionId, "D"),
        row.ValueOrdinal,
        row.LeadDays,
        DateTimeOffset.Parse(row.CreatedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static string Key(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed class ReminderRow
    {
        public required string ReminderId { get; init; }
        public string FieldValueId { get; init; } = string.Empty;
        public int ValueOrdinal { get; init; }
        public int LeadDays { get; init; }
        public required string CreatedAtUtc { get; init; }
        public required string RecordId { get; init; }
        public required string RecordTypeId { get; init; }
        public required string RecordTypeName { get; init; }
        public required string RecordDisplayName { get; init; }
        public required string FieldDefinitionId { get; init; }
        public required string FieldName { get; init; }
        public required string EventDate { get; init; }
    }
}
