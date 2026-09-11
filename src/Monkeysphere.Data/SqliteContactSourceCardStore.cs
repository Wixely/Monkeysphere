using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

/// <summary>
/// Rebuilds the vCard each contact was imported from, out of the source material the import
/// retained. This is what makes filling fields retroactively possible without the original file:
/// every property line was kept verbatim, so the card can be reconstructed exactly.
/// </summary>
internal sealed class SqliteContactSourceCardStore(
    MonkeysphereConnectionFactory connections,
    IBackstageVisibility visibility) : IContactSourceCardStore
{
    public async Task<IReadOnlyList<ContactSourceCard>> ListAsync(
        Guid recordTypeId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Only the most recent vCard import per record: a record merged from several cards is
        // filled from the card that most recently described it, not from an arbitrary mixture.
        IEnumerable<Row> rows = await connection.QueryAsync<Row>(new CommandDefinition($"""
            SELECT v.RecordId, v.Ordinal, v.Grouping, v.Name, v.ParametersJson, v.RawValue,
                   COALESCE(i.SourceFormat, '3.0') AS SourceFormat
            FROM RecordSourceValues v
            INNER JOIN Records r ON r.Id = v.RecordId
            LEFT JOIN RecordSourceImports i ON i.Id = v.ImportId
            WHERE r.RecordTypeId = @RecordTypeId{BackstageFilter.AndVisible(visibility, "r")}
              AND (
                  v.ImportId IS NULL
                  OR v.ImportId = (
                      SELECT latest.Id FROM RecordSourceImports latest
                      WHERE latest.RecordId = v.RecordId AND latest.SourceKind = 'vcard'
                      ORDER BY latest.ImportedAtUtc DESC, latest.Id DESC
                      LIMIT 1))
            ORDER BY v.RecordId, v.Ordinal;
            """,
            new { RecordTypeId = recordTypeId.ToString("D", CultureInfo.InvariantCulture) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        List<ContactSourceCard> cards = [];
        foreach (IGrouping<string, Row> group in rows.GroupBy(row => row.RecordId, StringComparer.Ordinal))
        {
            VCardProperty[] properties = group
                .OrderBy(row => row.Ordinal)
                .Select(row => new VCardProperty(
                    row.Grouping,
                    row.Name,
                    JsonSerializer.Deserialize<VCardParameter[]>(row.ParametersJson) ?? [],
                    row.RawValue))
                .ToArray();
            if (properties.Length == 0) continue;

            // The fingerprint is not recomputed: nothing here consumes it, and a value that looked
            // like an import fingerprint but was not one would be worse than none.
            cards.Add(new(
                Guid.ParseExact(group.Key, "D"),
                new VCard(group.First().SourceFormat, properties, string.Empty)));
        }

        return cards;
    }

    private sealed class Row
    {
        public required string RecordId { get; init; }
        public int Ordinal { get; init; }
        public string? Grouping { get; init; }
        public required string Name { get; init; }
        public required string ParametersJson { get; init; }
        public required string RawValue { get; init; }
        public required string SourceFormat { get; init; }
    }
}
