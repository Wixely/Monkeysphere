using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

/// <summary>
/// Reads the raw material an importer retained for a record. Nothing here interprets it: the point
/// of keeping it is that the application did not understand all of it, so it is handed back as it
/// arrived. Record visibility applies, so a record held back by backstage policy has no readable
/// source material either.
/// </summary>
internal sealed class SqliteRecordSourceStore(
    MonkeysphereConnectionFactory connections,
    IBackstageVisibility visibility) : IRecordSourceStore
{
    public async Task<RecordSourceSnapshot?> GetAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!await RecordIsReadableAsync(connection, recordId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        IEnumerable<ImportRow> importRows = await connection.QueryAsync<ImportRow>(new CommandDefinition("""
            SELECT Id, SourceKind, SourceFormat, Fingerprint, ImportedAtUtc
            FROM RecordSourceImports
            WHERE RecordId = @Id
            ORDER BY ImportedAtUtc DESC, Id;
            """, new { Id = Key(recordId) }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        // RawValue can be an embedded photo or key running to megabytes, so the listing takes its
        // length and a bounded prefix in SQL rather than pulling every value into memory.
        IEnumerable<ValueRow> valueRows = await connection.QueryAsync<ValueRow>(new CommandDefinition("""
            SELECT v.Ordinal, v.ImportId, v.Grouping, v.Name, v.ParametersJson,
                   LENGTH(v.RawValue) AS ValueLength,
                   SUBSTR(v.RawValue, 1, @PreviewLength) AS ValuePreview,
                   v.Mapping, v.FieldDefinitionId, fd.Name AS FieldName
            FROM RecordSourceValues v
            LEFT JOIN FieldDefinitions fd ON fd.Id = v.FieldDefinitionId
            WHERE v.RecordId = @Id
            ORDER BY v.Ordinal
            LIMIT @Limit;
            """,
            new
            {
                Id = Key(recordId),
                PreviewLength = RecordSourceLimits.PreviewLength,
                Limit = RecordSourceLimits.MaximumValues,
            }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        RecordSourceImport[] imports = importRows.Select(row => new RecordSourceImport(
            Parse(row.Id), recordId, row.SourceKind, row.SourceFormat, row.Fingerprint,
            ParseTimestamp(row.ImportedAtUtc))).ToArray();
        RecordSourceValue[] values = valueRows.Select(row => new RecordSourceValue(
            row.Ordinal,
            row.ImportId is null ? null : Parse(row.ImportId),
            row.Grouping,
            row.Name,
            JsonSerializer.Deserialize<RecordSourceParameter[]>(row.ParametersJson) ?? [],
            (RecordSourceMapping)row.Mapping,
            row.FieldDefinitionId is null ? null : Parse(row.FieldDefinitionId),
            row.FieldName,
            row.ValueLength,
            (row.ValuePreview ?? string.Empty).ReplaceLineEndings(" "),
            row.ValueLength > RecordSourceLimits.PreviewLength)).ToArray();

        return imports.Length == 0 && values.Length == 0 ? null : new(recordId, imports, values);
    }

    public async Task<string?> ReadValueAsync(Guid recordId, int ordinal, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!await RecordIsReadableAsync(connection, recordId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT RawValue FROM RecordSourceValues WHERE RecordId = @Id AND Ordinal = @Ordinal;",
            new { Id = Key(recordId), Ordinal = ordinal }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task<bool> RecordIsReadableAsync(SqliteConnection connection, Guid recordId, CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS(SELECT 1 FROM Records r WHERE r.Id = @Id{BackstageFilter.AndVisible(visibility, "r")});",
            new { Id = Key(recordId) }, cancellationToken: cancellationToken)).ConfigureAwait(false);

    private static string Key(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);
    private static Guid Parse(string value) => Guid.ParseExact(value, "D");
    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class ImportRow
    {
        public required string Id { get; init; }
        public required string SourceKind { get; init; }
        public string? SourceFormat { get; init; }
        public string? Fingerprint { get; init; }
        public required string ImportedAtUtc { get; init; }
    }

    private sealed class ValueRow
    {
        public int Ordinal { get; init; }
        public string? ImportId { get; init; }
        public string? Grouping { get; init; }
        public required string Name { get; init; }
        public required string ParametersJson { get; init; }
        public int ValueLength { get; init; }
        public string? ValuePreview { get; init; }
        public int Mapping { get; init; }
        public string? FieldDefinitionId { get; init; }
        public string? FieldName { get; init; }
    }
}
