using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

/// <summary>
/// Universal tags as one domain sees them. The values themselves are curated deployment-wide in
/// the tag catalogue; this reads what the domain's records hold and what its editor should offer.
/// </summary>
internal sealed class SqliteRecordTagStore(
    MonkeysphereConnectionFactory connections,
    IBackstageVisibility visibility,
    ITagCatalogue catalogue,
    ICurrentDomain currentDomain) : IRecordTagStore
{
    public async Task<IReadOnlyList<string>> ListVocabularyAsync(CancellationToken cancellationToken = default)
    {
        // Suggestions come from the catalogue rather than from what records happen to hold, so a
        // curated tag is offered before anything uses it, and a tag that exists only in another
        // domain is not offered here at all. That second half is deliberate: it is what stops the
        // suggestion list from revealing the contents of a sphere the reader is not looking at.
        IReadOnlyList<TagDefinition> available =
            await catalogue.ListAsync(currentDomain.Id, cancellationToken).ConfigureAwait(false);
        if (available.Count == 0) return [];

        // One exception, and it is the backstage one. A catalogue tag whose only use in this domain
        // is on hidden records would otherwise announce that such a record exists, and something of
        // its subject, to a reader who cannot see the record. An unused tag discloses nothing, so
        // only the used-but-entirely-hidden case is withheld.
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // OrdinalIgnoreCase, because the query that fills this compares COLLATE NOCASE: an ordinal
        // set would let a tag differing only in case slip past the guard and be suggested.
        HashSet<string> concealed = new(StringComparer.OrdinalIgnoreCase);
        concealed.UnionWith(await connection.QueryAsync<string>(new CommandDefinition($"""
            SELECT DISTINCT t.Value
            FROM RecordTags t
            JOIN Records r ON r.Id = t.RecordId
            WHERE r.BackstageState IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM RecordTags visible
                  JOIN Records vr ON vr.Id = visible.RecordId
                  WHERE visible.Value = t.Value COLLATE NOCASE
                    AND vr.BackstageState IS NULL);
            """, cancellationToken: cancellationToken)).ConfigureAwait(false));

        return
        [
            .. available
                .Select(tag => tag.Name)
                .Where(name => visibility.IncludeBackstageRecords || !concealed.Contains(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    public async Task<int> CountTaggedRecordsAsync(Guid recordTypeId, CancellationToken cancellationToken = default)
    {
        // Deliberately counts hidden records too. This answers "what would turning tags off put
        // out of reach?", and an administrator making that choice needs the true number; the same
        // reasoning already governs the structural guards that count hidden records.
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(DISTINCT t.RecordId)
            FROM RecordTags t
            JOIN Records r ON r.Id = t.RecordId
            WHERE r.RecordTypeId = @RecordTypeId;
            """,
            new { RecordTypeId = Key(recordTypeId) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static string Key(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
}
