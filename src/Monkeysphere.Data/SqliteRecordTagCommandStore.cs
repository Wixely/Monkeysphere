using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

/// <summary>
/// Changes only tags, on an explicit set of records. The ordinary record update would have done
/// this too, but only by reading every field value of every selected record and writing them all
/// back, which is a great many chances to overwrite something nobody meant to touch when all that
/// was wanted was one label.
/// </summary>
internal sealed class SqliteRecordTagCommandStore(
    MonkeysphereConnectionFactory connections,
    IBackstageVisibility visibility) : IRecordTagCommandStore
{
    public async Task<IReadOnlyList<RecordTagChange>> ApplyAsync(
        IReadOnlyList<RecordTagSelection> records,
        IReadOnlyList<ResolvedTag> add,
        IReadOnlyList<string> remove,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        List<RecordTagChange> changes = [];
        foreach (RecordTagSelection selection in records)
        {
            changes.Add(await ApplyOneAsync(connection, selection, add, remove, now, cancellationToken).ConfigureAwait(false));
        }

        return changes;
    }

    /// <summary>
    /// One record, one transaction. The selection deliberately is not all-or-nothing: a single
    /// record that moved underneath the operator should be reported as such, not used to discard
    /// the thirty-nine that were fine.
    /// </summary>
    private async Task<RecordTagChange> ApplyOneAsync(
        SqliteConnection connection,
        RecordTagSelection selection,
        IReadOnlyList<ResolvedTag> add,
        IReadOnlyList<string> remove,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();

        // The same visibility predicate every other read of Records carries. Without it this would
        // be a write surface that quietly confirms a hidden record exists, which is the one thing
        // backstage is absolute about.
        RecordTagRow? row = await connection.QuerySingleOrDefaultAsync<RecordTagRow>(new CommandDefinition($"""
            SELECT r.Id, r.DisplayName, r.Revision, t.TagsEnabled
            FROM Records r
            JOIN RecordTypes t ON t.Id = r.RecordTypeId
            WHERE r.Id = @RecordId{BackstageFilter.AndVisible(visibility, "r")};
            """, new { RecordId = Key(selection.RecordId) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is null)
        {
            return Outcome(selection.RecordId, RecordTagOutcome.NotFound, string.Empty, [], string.Empty);
        }

        if (selection.ExpectedRevision is not null && selection.ExpectedRevision != row.Revision)
        {
            return Outcome(selection.RecordId, RecordTagOutcome.Stale, row.DisplayName, [], row.Revision);
        }

        if (row.TagsEnabled == 0)
        {
            return Outcome(selection.RecordId, RecordTagOutcome.TagsDisabled, row.DisplayName, [], row.Revision);
        }

        List<TagRow> current =
            [.. await connection.QueryAsync<TagRow>(new CommandDefinition(
                "SELECT Value, TagId FROM RecordTags WHERE RecordId = @RecordId ORDER BY Ordinal;",
                new { RecordId = Key(selection.RecordId) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)];

        // Kept in the order the record already had them, with anything new appended. Rewriting the
        // order would show up as a change on every record the operator touched.
        List<TagRow> next = [.. current.Where(tag =>
            !remove.Contains(tag.Value, StringComparer.OrdinalIgnoreCase))];
        foreach (ResolvedTag tag in add)
        {
            if (!next.Any(existing => string.Equals(existing.Value, tag.Name, StringComparison.OrdinalIgnoreCase)))
            {
                next.Add(new(tag.Name, Key(tag.Id)));
            }
        }

        if (next.Count > RecordTagRules.MaximumCount)
        {
            throw new DomainValidationException(
                $"{row.DisplayName} would carry more than {RecordTagRules.MaximumCount} tags.");
        }

        if (next.Select(tag => tag.Value).SequenceEqual(current.Select(tag => tag.Value), StringComparer.Ordinal))
        {
            // Nothing to write, and deliberately no revision bump: a record that already carried
            // the tag has not been edited, and saying it was would invalidate revisions the
            // operator is still holding for the records that genuinely did not change.
            return Outcome(selection.RecordId, RecordTagOutcome.Unchanged, row.DisplayName,
                [.. current.Select(tag => tag.Value)], row.Revision);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM RecordTags WHERE RecordId = @RecordId;",
            new { RecordId = Key(selection.RecordId) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        for (int ordinal = 0; ordinal < next.Count; ordinal++)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO RecordTags (RecordId, Ordinal, Value, TagId) VALUES (@RecordId, @Ordinal, @Value, @TagId);",
                new
                {
                    RecordId = Key(selection.RecordId),
                    Ordinal = ordinal,
                    next[ordinal].Value,
                    next[ordinal].TagId,
                }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Records SET UpdatedAtUtc = @Now WHERE Id = @RecordId;",
            new { RecordId = Key(selection.RecordId), Now = Timestamp(now) },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Read the revision back inside the transaction, so what the caller is handed is the one
        // the tag triggers have just written rather than the one being replaced.
        string revision = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT Revision FROM Records WHERE Id = @RecordId;",
            new { RecordId = Key(selection.RecordId) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)
            ?? string.Empty;

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Outcome(selection.RecordId, RecordTagOutcome.Applied, row.DisplayName,
            [.. next.Select(tag => tag.Value)], revision);
    }

    private static RecordTagChange Outcome(
        Guid recordId, RecordTagOutcome outcome, string displayName, IReadOnlyList<string> tags, string revision) =>
        new(recordId, outcome, displayName, tags, revision);

    private static string Key(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record RecordTagRow(string Id, string DisplayName, string Revision, long TagsEnabled);

    private sealed record TagRow(string Value, string? TagId);
}
