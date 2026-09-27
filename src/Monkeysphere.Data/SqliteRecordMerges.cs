using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed partial class SqliteMonkeysphereStore : IRecordMergeStore
{
    /// <summary>
    /// Everything a merge would do, worked out once. The preview and the apply both run this, so the
    /// numbers somebody agrees to are the numbers that happen rather than two calculations that have
    /// to be kept in step.
    /// </summary>
    private sealed record MergePlan(
        RecordDetails Surviving,
        RecordDetails Merged,
        string Revision,
        string? Refusal,
        IReadOnlyList<RecordMergeValueConflict> Conflicts,
        IReadOnlyList<RecordMergeUncarriedValue> Archived,
        IReadOnlyList<MergeFieldAction> Actions,
        IReadOnlyList<string> AliasesAdded,
        IReadOnlyList<string> TagsAdded,
        IReadOnlyList<MergeRelationshipMove> Relationships,
        IReadOnlyList<MergeReminderMove> Reminders);

    /// <summary>
    /// What happens to one field. <see cref="FirstOrdinal"/> is where the first carried value lands on
    /// the survivor; the rest follow it in order. It is decided here rather than at write time because
    /// reminders point at a value by its ordinal, so moving a value renumbers its reminder too, and
    /// both have to agree on the same number.
    /// </summary>
    private sealed record MergeFieldAction(
        Guid FieldDefinitionId,
        bool ReplaceSurvivingValues,
        IReadOnlyList<RecordValue> Carry,
        bool UnionTags,
        int FirstOrdinal);

    /// <summary>
    /// One relationship of the merged-away record, and where it ends up. Dropped ones are worked out
    /// here rather than in SQL because whether two relationships mean the same thing depends on the
    /// type's directionality, and because a symmetric pair's stored order follows
    /// <see cref="Guid.CompareTo(Guid)"/>, which is not the order SQLite would compare the text in.
    /// </summary>
    private sealed record MergeRelationshipMove(Guid Id, bool Drop, Guid Source, Guid Target);

    /// <summary>
    /// One reminder, on either record, and its fate. A reminder names its value by field and ordinal,
    /// so a carried value's reminder has to be renumbered to follow it, a reminder on a value that is
    /// not carried has nothing left to remind anybody about, and the survivor's own reminders go when
    /// its values are replaced.
    /// </summary>
    private sealed record MergeReminderMove(Guid Id, bool Drop, int ValueOrdinal);

    public async Task<RecordMergePreview?> PreviewRecordMergeAsync(Guid survivingRecordId, Guid mergedRecordId,
        IReadOnlyList<RecordMergeChoice>? choices, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        MergePlan? plan = await BuildMergePlanAsync(connection, transaction, survivingRecordId, mergedRecordId,
            choices ?? [], cancellationToken).ConfigureAwait(false);
        if (plan is null) return null;
        RecordMergeImpact impact = await CountMergeImpactAsync(connection, transaction, plan, cancellationToken).ConfigureAwait(false);
        return new(plan.Surviving.Record, plan.Merged.Record, plan.Revision, plan.Refusal, impact,
            Cap(plan.Conflicts), Cap(plan.Archived), plan.AliasesAdded, plan.TagsAdded);
    }

    public async Task MergeRecordsAsync(Guid survivingRecordId, Guid mergedRecordId,
        IReadOnlyList<RecordMergeChoice> choices, string expectedRevision, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ApplyMergeAsync(connection, transaction, survivingRecordId, mergedRecordId, choices, expectedRevision,
            now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The remote merge. The plan is built and applied inside the receipt's own transaction, so a
    /// command that is recorded as done is a command that happened, and one that was refused left
    /// nothing behind.
    /// </summary>
    public Task<RecordCommandReceipt> MergeRecordsAsync(RecordCommandIdentity identity, Guid survivingRecordId,
        Guid mergedRecordId, IReadOnlyList<RecordMergeChoice> choices, string expectedRevision, DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, "records.merge", now, async (connection, transaction) =>
        {
            RecordDetails surviving = await ApplyMergeAsync(connection, transaction, survivingRecordId, mergedRecordId,
                choices, expectedRevision, now, cancellationToken).ConfigureAwait(false);

            // The receipt names the record that still exists and the revision it now carries, because
            // that is the thing a caller can go on to read. The merged-away one is reported as deleted
            // rather than left out, so a replayed receipt still says what became of it.
            string revision = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT Revision FROM Records WHERE Id = @Id;", new { Id = Key(survivingRecordId) }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)
                ?? surviving.Revision;
            return (IReadOnlyList<RecordMutationOutcome>)
            [
                new(survivingRecordId, revision, "merged"),
                new(mergedRecordId, string.Empty, "deleted"),
            ];
        }, cancellationToken);

    /// <summary>
    /// Shared by the browser and the remote surface so the two cannot come to disagree about when a
    /// merge is allowed. Returns the surviving record as the plan saw it, before the merge ran.
    /// </summary>
    private async Task<RecordDetails> ApplyMergeAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid survivingRecordId, Guid mergedRecordId, IReadOnlyList<RecordMergeChoice> choices, string expectedRevision,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        MergePlan plan = await BuildMergePlanAsync(connection, transaction, survivingRecordId, mergedRecordId,
            choices, cancellationToken).ConfigureAwait(false)
            ?? throw new RecordCommandNotFoundException("One or both records were not found.");
        if (plan.Refusal is not null) throw new DomainValidationException(plan.Refusal);
        if (!string.Equals(plan.Revision, expectedRevision, StringComparison.Ordinal))
        {
            throw new ConcurrencyConflictException(
                "One of these records changed after the merge was previewed. Preview it again before merging.");
        }

        await MergeRecordsCoreAsync(connection, transaction, plan, now, cancellationToken).ConfigureAwait(false);
        return plan.Surviving;
    }

    private static IReadOnlyList<T> Cap<T>(IReadOnlyList<T> values) =>
        values.Count <= RecordMergeLimits.MaximumReportedValues
            ? values
            : [.. values.Take(RecordMergeLimits.MaximumReportedValues)];

    private async Task<MergePlan?> BuildMergePlanAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid survivingRecordId, Guid mergedRecordId, IReadOnlyList<RecordMergeChoice> choices, CancellationToken cancellationToken)
    {
        // The same visibility predicate as any other read, so a merge cannot be used to establish that
        // a record the caller may not see exists, let alone to fold it into one they can.
        string visible = RecordsVisible("r");
        RecordDetails? surviving = await QueryRecordAsync(connection, transaction, survivingRecordId, cancellationToken, visible).ConfigureAwait(false);
        RecordDetails? merged = await QueryRecordAsync(connection, transaction, mergedRecordId, cancellationToken, visible).ConfigureAwait(false);
        if (surviving is null || merged is null) return null;

        string revision = await ComputeMergeRevisionAsync(connection, transaction, survivingRecordId, mergedRecordId, cancellationToken).ConfigureAwait(false);
        string? refusal = survivingRecordId == mergedRecordId
            ? "A record cannot be merged into itself. Choose a different record."
            : null;

        Dictionary<Guid, RecordMergeResolution> chosen = choices
            .GroupBy(choice => choice.FieldDefinitionId)
            .ToDictionary(group => group.Key, group => group.Last().Resolution);

        // The survivor's type decides where a value can live. Merging across types is allowed, so a
        // field the survivor's type does not carry has nowhere to go as record data and is archived.
        HashSet<Guid> survivingFields = [.. surviving.AvailableFields.Select(field => field.Definition.Id)];
        Dictionary<Guid, RecordTypeField> known = surviving.AvailableFields.Concat(merged.AvailableFields)
            .GroupBy(field => field.Definition.Id)
            .ToDictionary(group => group.Key, group => group.First());

        List<RecordMergeValueConflict> conflicts = [];
        List<RecordMergeUncarriedValue> archived = [];
        List<MergeFieldAction> actions = [];

        foreach (IGrouping<Guid, RecordValue> group in merged.Values.GroupBy(value => value.FieldDefinitionId))
        {
            RecordValue[] fromMerged = [.. group.OrderBy(value => value.Ordinal)];
            RecordValue[] fromSurviving = [.. surviving.Values.Where(value => value.FieldDefinitionId == group.Key).OrderBy(value => value.Ordinal)];
            string name = known.TryGetValue(group.Key, out RecordTypeField? field) ? field.Definition.Name : fromMerged[0].FieldName;
            string typeId = field?.Definition.TypeId ?? fromMerged[0].TypeId;

            if (!survivingFields.Contains(group.Key))
            {
                archived.Add(new(group.Key, name, RecordMergeUncarriedReasons.FieldNotOnSurvivingType, fromMerged));
                continue;
            }

            int append = fromSurviving.Length == 0 ? 0 : fromSurviving[^1].Ordinal + 1;
            if (fromSurviving.Length == 0)
            {
                // Nothing to decide: the survivor has no opinion about this field.
                actions.Add(new(group.Key, ReplaceSurvivingValues: false, fromMerged, UnionTags: false, FirstOrdinal: 0));
                continue;
            }

            RecordMergeResolution resolution = chosen.GetValueOrDefault(group.Key, RecordMergeResolution.KeepSurviving);
            conflicts.Add(new(group.Key, name, typeId, fromSurviving, fromMerged, resolution));
            switch (resolution)
            {
                case RecordMergeResolution.TakeMerged:
                    // The survivor's values go, so the incoming ones start from the bottom.
                    actions.Add(new(group.Key, ReplaceSurvivingValues: true, fromMerged, UnionTags: false, FirstOrdinal: 0));
                    archived.Add(new(group.Key, name, RecordMergeUncarriedReasons.SurvivingValueReplaced, fromSurviving));
                    break;
                case RecordMergeResolution.KeepBoth when typeId == FieldTypes.Tags:
                    // Two lists on one field would read as two sets of tags rather than one. Combining
                    // them is what keeping both means here, so no value moves and nothing is renumbered.
                    actions.Add(new(group.Key, ReplaceSurvivingValues: false, fromMerged, UnionTags: true, FirstOrdinal: append));
                    break;
                case RecordMergeResolution.KeepBoth:
                    actions.Add(new(group.Key, ReplaceSurvivingValues: false, fromMerged, UnionTags: false, FirstOrdinal: append));
                    break;
                default:
                    archived.Add(new(group.Key, name, RecordMergeUncarriedReasons.SurvivingValueKept, fromMerged));
                    break;
            }
        }

        // A different name is not a disagreement to resolve; it is another name the same person goes
        // by, which is what aliases are for. Compared without case because the alias index is.
        HashSet<string> existingNames = new(surviving.Aliases.Append(surviving.Record.DisplayName), StringComparer.OrdinalIgnoreCase);
        List<string> aliasesAdded = [];
        foreach (string candidate in merged.Aliases.Prepend(merged.Record.DisplayName))
        {
            if (!string.IsNullOrWhiteSpace(candidate) && existingNames.Add(candidate)) aliasesAdded.Add(candidate);
        }

        HashSet<string> existingTags = new(surviving.Tags, StringComparer.OrdinalIgnoreCase);
        List<string> tagsAdded = [.. merged.Tags.Where(tag => existingTags.Add(tag))];

        IReadOnlyList<MergeRelationshipMove> relationships = await PlanRelationshipMovesAsync(
            connection, transaction, survivingRecordId, mergedRecordId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<MergeReminderMove> reminders = await PlanReminderMovesAsync(
            connection, transaction, survivingRecordId, mergedRecordId, actions, cancellationToken).ConfigureAwait(false);

        return new(surviving, merged, revision, refusal, conflicts, archived, actions, aliasesAdded, tagsAdded,
            relationships, reminders);
    }

    /// <summary>
    /// A fingerprint over both records and everything a merge would move.
    ///
    /// <c>DeletionRevision</c> is maintained by triggers across every table that hangs off a record,
    /// which is exactly the surface a merge touches, so it already answers "has anything relevant
    /// changed" without a second set of triggers being invented for this.
    /// </summary>
    private static async Task<string> ComputeMergeRevisionAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid survivingRecordId, Guid mergedRecordId, CancellationToken cancellationToken)
    {
        IEnumerable<string> rows = await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT Id || '|' || Revision || '|' || DeletionRevision
            FROM Records WHERE Id IN @Ids ORDER BY Id;
            """, new { Ids = new[] { Key(survivingRecordId), Key(mergedRecordId) } }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows))));
    }

    private sealed record MergeRelationshipRow(string Id, string RelationshipTypeId, string SourceRecordId,
        string TargetRecordId, long Directionality);

    /// <summary>
    /// Works out which of the merged-away record's relationships can be repointed at the survivor.
    ///
    /// Two cannot. A relationship between the two records would become the survivor related to itself,
    /// which the table forbids outright and which means nothing anyway. And one duplicating a
    /// relationship the survivor already has — same type, same other record, and for a directional
    /// type the same way round — would collide with it on the unique index. Both are dropped in favour
    /// of what the survivor already holds.
    /// </summary>
    private static async Task<IReadOnlyList<MergeRelationshipMove>> PlanRelationshipMovesAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid survivingRecordId, Guid mergedRecordId,
        CancellationToken cancellationToken)
    {
        IEnumerable<MergeRelationshipRow> rows = await connection.QueryAsync<MergeRelationshipRow>(new CommandDefinition("""
            SELECT rel.Id, rel.RelationshipTypeId, rel.SourceRecordId, rel.TargetRecordId, rt.Directionality
            FROM Relationships rel
            JOIN RelationshipTypes rt ON rt.Id = rel.RelationshipTypeId
            WHERE rel.SourceRecordId IN @Ids OR rel.TargetRecordId IN @Ids
            ORDER BY rel.Id;
            """, new { Ids = new[] { Key(survivingRecordId), Key(mergedRecordId) } }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        HashSet<(Guid Type, Guid Source, Guid Target)> taken = [];
        List<MergeRelationshipMove> moves = [];
        List<MergeRelationshipRow> ours = [];

        // The survivor's own relationships are registered first so an incoming duplicate loses to them
        // rather than the other way round.
        foreach (MergeRelationshipRow row in rows)
        {
            Guid source = Guid.Parse(row.SourceRecordId, CultureInfo.InvariantCulture);
            Guid target = Guid.Parse(row.TargetRecordId, CultureInfo.InvariantCulture);
            if (source == mergedRecordId || target == mergedRecordId) ours.Add(row);
            else taken.Add((Guid.Parse(row.RelationshipTypeId, CultureInfo.InvariantCulture), source, target));
        }

        foreach (MergeRelationshipRow row in ours)
        {
            Guid id = Guid.Parse(row.Id, CultureInfo.InvariantCulture);
            Guid type = Guid.Parse(row.RelationshipTypeId, CultureInfo.InvariantCulture);
            Guid source = Guid.Parse(row.SourceRecordId, CultureInfo.InvariantCulture);
            Guid target = Guid.Parse(row.TargetRecordId, CultureInfo.InvariantCulture);
            if (source == survivingRecordId || target == survivingRecordId)
            {
                moves.Add(new(id, Drop: true, survivingRecordId, survivingRecordId));
                continue;
            }

            if (source == mergedRecordId) source = survivingRecordId;
            if (target == mergedRecordId) target = survivingRecordId;
            if (row.Directionality == (long)RelationshipDirectionality.Symmetric && source.CompareTo(target) > 0)
            {
                // A symmetric type keeps its two ends in a fixed order so one pair cannot also be
                // stored the other way round. Repointing can put them out of that order.
                (source, target) = (target, source);
            }

            // Registered as it is accepted, so two of the merged record's own relationships collapsing
            // onto the same pair drop one of themselves rather than colliding at write time.
            moves.Add(new(id, Drop: !taken.Add((type, source, target)), source, target));
        }

        return moves;
    }

    private sealed record MergeReminderRow(string Id, string RecordId, string FieldDefinitionId, long ValueOrdinal);

    /// <summary>
    /// Works out what happens to both records' reminders.
    ///
    /// A reminder names the value it is about by field and ordinal rather than by identity, so it does
    /// not follow its value automatically: carried values are renumbered on the survivor and their
    /// reminders have to be renumbered with them, or a reminder would quietly come to be about
    /// whichever value happens to now sit at its old number. A reminder whose value is not carried is
    /// dropped, because the thing it was about is no longer live data on any record.
    /// </summary>
    private static async Task<IReadOnlyList<MergeReminderMove>> PlanReminderMovesAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid survivingRecordId, Guid mergedRecordId,
        IReadOnlyList<MergeFieldAction> actions, CancellationToken cancellationToken)
    {
        IEnumerable<MergeReminderRow> rows = await connection.QueryAsync<MergeReminderRow>(new CommandDefinition("""
            SELECT Id, RecordId, FieldDefinitionId, ValueOrdinal FROM Reminders
            WHERE RecordId IN @Ids ORDER BY Id;
            """, new { Ids = new[] { Key(survivingRecordId), Key(mergedRecordId) } }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        Dictionary<Guid, MergeFieldAction> byField = actions.ToDictionary(action => action.FieldDefinitionId);
        HashSet<Guid> replaced = [.. actions.Where(action => action.ReplaceSurvivingValues).Select(action => action.FieldDefinitionId)];
        HashSet<(Guid Field, int Ordinal)> taken = [];
        List<MergeReminderMove> moves = [];
        List<MergeReminderRow> ours = [];

        foreach (MergeReminderRow row in rows)
        {
            Guid field = Guid.Parse(row.FieldDefinitionId, CultureInfo.InvariantCulture);
            if (string.Equals(row.RecordId, Key(mergedRecordId), StringComparison.OrdinalIgnoreCase))
            {
                ours.Add(row);
                continue;
            }

            if (replaced.Contains(field))
            {
                // The value this was about is being replaced by the other record's, so there is nothing
                // left for it to fall due on.
                moves.Add(new(Guid.Parse(row.Id, CultureInfo.InvariantCulture), Drop: true, (int)row.ValueOrdinal));
                continue;
            }

            taken.Add((field, (int)row.ValueOrdinal));
        }

        foreach (MergeReminderRow row in ours)
        {
            Guid id = Guid.Parse(row.Id, CultureInfo.InvariantCulture);
            Guid field = Guid.Parse(row.FieldDefinitionId, CultureInfo.InvariantCulture);
            if (!byField.TryGetValue(field, out MergeFieldAction? action) || action.UnionTags)
            {
                moves.Add(new(id, Drop: true, (int)row.ValueOrdinal));
                continue;
            }

            int index = -1;
            for (int i = 0; i < action.Carry.Count; i++)
            {
                if (action.Carry[i].Ordinal == row.ValueOrdinal) { index = i; break; }
            }

            if (index < 0)
            {
                moves.Add(new(id, Drop: true, (int)row.ValueOrdinal));
                continue;
            }

            int ordinal = action.FirstOrdinal + index;

            // The reminder index is unique on field, ordinal and lead time. Two reminders about the
            // same value are the same reminder, so the survivor's own is the one that stays.
            moves.Add(new(id, Drop: !taken.Add((field, ordinal)), ordinal));
        }

        return moves;
    }

    private static async Task<RecordMergeImpact> CountMergeImpactAsync(SqliteConnection connection, SqliteTransaction transaction,
        MergePlan plan, CancellationToken cancellationToken)
    {
        object ids = new { Surviving = Key(plan.Surviving.Record.Id), Merged = Key(plan.Merged.Record.Id) };
        using SqlMapper.GridReader counts = await connection.QueryMultipleAsync(new CommandDefinition("""
            SELECT COUNT(*) FROM RecordSourceImports WHERE RecordId = @Merged;
            SELECT COUNT(DISTINCT GraphViewId) FROM (
                SELECT GraphViewId FROM GraphViewRecords WHERE RecordId = @Merged
                UNION SELECT GraphViewId FROM GraphViewNodePositions WHERE RecordId = @Merged);
            """, ids, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        int sourceImports = await counts.ReadSingleAsync<int>().ConfigureAwait(false);
        int graphViews = await counts.ReadSingleAsync<int>().ConfigureAwait(false);

        // Relationships and reminders are counted from the plan rather than recounted in SQL, so the
        // preview cannot say one thing and the merge do another.
        return new(
            FieldValuesCarried: plan.Actions.Where(action => !action.UnionTags).Sum(action => action.Carry.Count),
            FieldValuesArchivedOnly: plan.Archived.Sum(entry => entry.Values.Count),
            AliasesAdded: plan.AliasesAdded.Count,
            TagsAdded: plan.TagsAdded.Count,
            ImagesCarried: plan.Merged.Images.Count,
            RelationshipsRepointed: plan.Relationships.Count(move => !move.Drop),
            RelationshipsDropped: plan.Relationships.Count(move => move.Drop),
            RemindersCarried: plan.Reminders.Count(move => !move.Drop),
            RemindersDropped: plan.Reminders.Count(move => move.Drop),
            SourceImportsCarried: sourceImports,
            GraphViewsUpdated: graphViews);
    }

    /// <summary>
    /// Carries out the plan. The order matters: everything worth keeping is moved onto the survivor
    /// before the merged record is deleted, because every table hanging off a record cascades, so
    /// anything still pointing at it when the row goes is gone without a word.
    /// </summary>
    private static async Task MergeRecordsCoreAsync(SqliteConnection connection, SqliteTransaction transaction,
        MergePlan plan, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string surviving = Key(plan.Surviving.Record.Id);
        string merged = Key(plan.Merged.Record.Id);
        string timestamp = Timestamp(now);
        object ids = new { Surviving = surviving, Merged = merged, Now = timestamp };

        // Reminders first, while the ordinals they were planned against are still the ordinals in the
        // table: dropping one after its value has already moved would need the plan read backwards.
        await ApplyReminderMovesAsync(connection, transaction, plan, surviving, cancellationToken).ConfigureAwait(false);
        await MoveFieldValuesAsync(connection, transaction, plan, surviving, timestamp, cancellationToken).ConfigureAwait(false);

        // Aliases and tags are both unique per record and compared without case, and the plan already
        // removed what the survivor has, so these append what is genuinely new.
        await AppendOrdinalRowsAsync(connection, transaction, "RecordAliases", surviving, plan.AliasesAdded, cancellationToken).ConfigureAwait(false);
        await AppendOrdinalRowsAsync(connection, transaction, "RecordTags", surviving, plan.TagsAdded, cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition("""
            -- Images are always kept, both records' worth: a person can have many. Only the cover is
            -- singular, and the survivor's own wins, so a moved cover becomes an ordinary image
            -- rather than colliding with the one-cover-per-record index.
            UPDATE RecordImages
            SET IsCover = 0
            WHERE RecordId = @Merged
              AND EXISTS (SELECT 1 FROM RecordImages c WHERE c.RecordId = @Surviving AND c.IsCover = 1);

            UPDATE RecordImages
            SET RecordId = @Surviving,
                Ordinal = Ordinal + (SELECT COALESCE(MAX(e.Ordinal) + 1, 0) FROM RecordImages e WHERE e.RecordId = @Surviving)
            WHERE RecordId = @Merged;
            """, ids, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await ApplyRelationshipMovesAsync(connection, transaction, plan, timestamp, cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition("""
            -- A saved graph view naming the merged record should now name the survivor, unless it
            -- already does, in which case the selection collapses to one node rather than two.
            DELETE FROM GraphViewRecords AS m
            WHERE m.RecordId = @Merged
              AND EXISTS (SELECT 1 FROM GraphViewRecords s WHERE s.GraphViewId = m.GraphViewId AND s.RecordId = @Surviving);
            UPDATE GraphViewRecords SET RecordId = @Surviving WHERE RecordId = @Merged;

            DELETE FROM GraphViewNodePositions AS m
            WHERE m.RecordId = @Merged
              AND EXISTS (SELECT 1 FROM GraphViewNodePositions s WHERE s.GraphViewId = m.GraphViewId AND s.RecordId = @Surviving);
            UPDATE GraphViewNodePositions SET RecordId = @Surviving WHERE RecordId = @Merged;
            """, ids, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await MoveSourceMaterialAsync(connection, transaction, ids, cancellationToken).ConfigureAwait(false);
        await ArchiveMergedRecordAsync(connection, transaction, plan, surviving, timestamp, cancellationToken).ConfigureAwait(false);

        // Last, and only now that nothing points at it: the cascade takes whatever was deliberately
        // left behind, which by this point is nothing anybody asked to keep.
        int removed = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Records WHERE Id = @Merged;", ids, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        RequireChanged(removed, "The record being merged away was not found.");
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Records SET UpdatedAtUtc = @Now WHERE Id = @Surviving;", ids, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static async Task ApplyReminderMovesAsync(SqliteConnection connection, SqliteTransaction transaction,
        MergePlan plan, string surviving, CancellationToken cancellationToken)
    {
        foreach (MergeReminderMove move in plan.Reminders.Where(move => move.Drop))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM Reminders WHERE Id = @Id;", new { Id = Key(move.Id) }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        // Moved out of the way first, because a reminder landing on its final ordinal could otherwise
        // collide with one that has not been moved off it yet. Negative ordinals would fail the
        // table's own check, so the parking numbers run above every ordinal in use.
        MergeReminderMove[] carried = [.. plan.Reminders.Where(move => !move.Drop)];
        if (carried.Length == 0) return;

        int park = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COALESCE(MAX(ValueOrdinal) + 1, 0) FROM Reminders;", transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (MergeReminderMove move in carried)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE Reminders SET RecordId = @Surviving, ValueOrdinal = @Ordinal WHERE Id = @Id;",
                new { Surviving = surviving, Ordinal = park++, Id = Key(move.Id) }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        foreach (MergeReminderMove move in carried)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE Reminders SET ValueOrdinal = @Ordinal WHERE Id = @Id;",
                new { Ordinal = move.ValueOrdinal, Id = Key(move.Id) }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    private static async Task ApplyRelationshipMovesAsync(SqliteConnection connection, SqliteTransaction transaction,
        MergePlan plan, string timestamp, CancellationToken cancellationToken)
    {
        foreach (MergeRelationshipMove move in plan.Relationships.Where(move => move.Drop))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM Relationships WHERE Id = @Id;", new { Id = Key(move.Id) }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        foreach (MergeRelationshipMove move in plan.Relationships.Where(move => !move.Drop))
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE Relationships SET SourceRecordId = @Source, TargetRecordId = @Target, UpdatedAtUtc = @Now
                WHERE Id = @Id;
                """, new { Source = Key(move.Source), Target = Key(move.Target), Now = timestamp, Id = Key(move.Id) },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    private static async Task MoveFieldValuesAsync(SqliteConnection connection, SqliteTransaction transaction,
        MergePlan plan, string surviving, string timestamp, CancellationToken cancellationToken)
    {
        foreach (MergeFieldAction action in plan.Actions)
        {
            string field = Key(action.FieldDefinitionId);
            if (action.ReplaceSurvivingValues)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM FieldValues WHERE RecordId = @Surviving AND FieldDefinitionId = @Field;",
                    new { Surviving = surviving, Field = field }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            if (action.UnionTags)
            {
                // One tags value holds a list, so keeping both means one list with both sets in it.
                // The survivor's own entries keep their order and the new ones follow; the unique
                // index on value drops anything both lists already had.
                foreach (RecordValue value in action.Carry)
                {
                    await connection.ExecuteAsync(new CommandDefinition("""
                        INSERT OR IGNORE INTO FieldValueTags (FieldValueId, Ordinal, Value)
                        SELECT target.Id,
                               (SELECT COALESCE(MAX(e.Ordinal) + 1, 0) FROM FieldValueTags e WHERE e.FieldValueId = target.Id)
                                   + ROW_NUMBER() OVER (ORDER BY incoming.Ordinal) - 1,
                               incoming.Value
                        FROM FieldValueTags incoming
                        JOIN FieldValues target ON target.RecordId = @Surviving AND target.FieldDefinitionId = @Field
                        WHERE incoming.FieldValueId = @Incoming
                          AND target.Ordinal = (SELECT MIN(o.Ordinal) FROM FieldValues o
                                                WHERE o.RecordId = @Surviving AND o.FieldDefinitionId = @Field)
                          AND incoming.Value NOT IN (SELECT e.Value FROM FieldValueTags e WHERE e.FieldValueId = target.Id);
                        """, new { Surviving = surviving, Field = field, Incoming = Key(value.Id) },
                        transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                }

                continue;
            }

            // Moved onto the ordinals the plan chose, because the values table is unique on record,
            // field and ordinal, the two records number their values independently, and the reminders
            // already moved were renumbered against exactly these.
            for (int i = 0; i < action.Carry.Count; i++)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE FieldValues SET RecordId = @Surviving, Ordinal = @Ordinal, UpdatedAtUtc = @Now WHERE Id = @Id;",
                    new { Surviving = surviving, Ordinal = action.FirstOrdinal + i, Now = timestamp, Id = Key(action.Carry[i].Id) },
                    transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }
    }

    private static async Task AppendOrdinalRowsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, string recordId, IReadOnlyList<string> values, CancellationToken cancellationToken)
    {
        if (values.Count == 0) return;
        int next = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COALESCE(MAX(Ordinal) + 1, 0) FROM {table} WHERE RecordId = @RecordId;",
            new { RecordId = recordId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (string value in values)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                $"INSERT OR IGNORE INTO {table} (RecordId, Ordinal, Value) VALUES (@RecordId, @Ordinal, @Value);",
                new { RecordId = recordId, Ordinal = next++, Value = value }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    private static async Task MoveSourceMaterialAsync(SqliteConnection connection, SqliteTransaction transaction,
        object ids, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(new CommandDefinition("""
            -- Both records having been imported from the same card is exactly the situation that
            -- produces duplicates, so the survivor may already hold that import. Its own is kept.
            DELETE FROM RecordSourceImports AS m
            WHERE m.RecordId = @Merged AND m.Fingerprint IS NOT NULL
              AND EXISTS (SELECT 1 FROM RecordSourceImports s
                          WHERE s.RecordId = @Surviving AND s.SourceKind = m.SourceKind AND s.Fingerprint = m.Fingerprint);
            UPDATE RecordSourceImports SET RecordId = @Surviving WHERE RecordId = @Merged;

            -- Retained lines are numbered per record, so they continue after the survivor's own.
            UPDATE RecordSourceValues
            SET RecordId = @Surviving,
                Ordinal = Ordinal + (SELECT COALESCE(MAX(e.Ordinal) + 1, 0) FROM RecordSourceValues e WHERE e.RecordId = @Surviving)
            WHERE RecordId = @Merged;

            -- A line claiming to be the provenance of a field value the survivor's own line already
            -- claims cannot keep that claim: at most one line per field and ordinal. It stays, as
            -- material understood by nothing, which is what it now is.
            UPDATE RecordSourceValues AS m
            SET Mapping = 0, FieldDefinitionId = NULL, ValueOrdinal = NULL
            WHERE m.RecordId = @Surviving AND m.FieldDefinitionId IS NOT NULL
              AND EXISTS (SELECT 1 FROM RecordSourceValues s
                          WHERE s.RecordId = m.RecordId AND s.FieldDefinitionId = m.FieldDefinitionId
                            AND s.ValueOrdinal = m.ValueOrdinal AND s.Ordinal < m.Ordinal);
            """, ids, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Writes the merged-away record down in full, so that nothing it held is only recoverable from a
    /// backup. This is the promise that makes a merge different from copying a few fields across and
    /// deleting the rest, and it lands in the same place an unrecognised vCard line lands, so the one
    /// screen that already shows a record's original data shows this too.
    ///
    /// Field values are archived as opaque material rather than claimed as the provenance of the
    /// survivor's values. The archive says what the deleted record held; where a value was carried the
    /// survivor's own live value already says the rest, and where it was not, this is the only trace
    /// and is not provenance for anything.
    /// </summary>
    private static async Task ArchiveMergedRecordAsync(SqliteConnection connection, SqliteTransaction transaction,
        MergePlan plan, string surviving, string timestamp, CancellationToken cancellationToken)
    {
        string importId = Key(Guid.CreateVersion7());
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO RecordSourceImports (Id, RecordId, SourceKind, SourceFormat, Fingerprint, ImportedAtUtc)
            VALUES (@Id, @RecordId, @Kind, @Format, NULL, @Now);
            """, new
        {
            Id = importId,
            RecordId = surviving,
            Kind = RecordSourceKinds.Merge,
            // The kind says a merge happened; the format says what was merged, which is the only place
            // the deleted record's own name and type survive as a heading rather than as a line.
            Format = $"{plan.Merged.Record.DisplayName} ({plan.Merged.Record.RecordTypeName})",
            Now = timestamp,
        }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        int next = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COALESCE(MAX(Ordinal) + 1, 0) FROM RecordSourceValues WHERE RecordId = @RecordId;",
            new { RecordId = surviving }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        List<(string Name, string Raw, int Mapping)> lines =
            [("DisplayName", plan.Merged.Record.DisplayName, (int)RecordSourceMapping.DisplayName)];
        lines.AddRange(plan.Merged.Aliases.Select(alias => ("Alias", alias, (int)RecordSourceMapping.Aliases)));
        lines.AddRange(plan.Merged.Tags.Select(tag => ("Tag", tag, (int)RecordSourceMapping.Opaque)));
        lines.AddRange(plan.Merged.Values
            .OrderBy(value => value.FieldName, StringComparer.Ordinal).ThenBy(value => value.Ordinal)
            .Select(value => (value.FieldName, Describe(value), (int)RecordSourceMapping.Opaque)));

        // A value of the survivor's own that a choice displaced. It is about to stop being record data
        // and this is the only place it will exist, so leaving it out would be the one way a merge could
        // still lose something somebody was looking at when they agreed to it.
        lines.AddRange(plan.Archived
            .Where(entry => entry.Reason == RecordMergeUncarriedReasons.SurvivingValueReplaced)
            .SelectMany(entry => entry.Values.OrderBy(value => value.Ordinal)
                .Select(value => ($"{entry.FieldName} (replaced)", Describe(value), (int)RecordSourceMapping.Opaque))));

        foreach ((string name, string raw, int mapping) in lines)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO RecordSourceValues
                    (RecordId, Ordinal, ImportId, Grouping, Name, ParametersJson, RawValue, Mapping, FieldDefinitionId, ValueOrdinal)
                VALUES (@RecordId, @Ordinal, @ImportId, NULL, @Name, '[]', @Raw, @Mapping, NULL, NULL);
                """, new { RecordId = surviving, Ordinal = next++, ImportId = importId, Name = name, Raw = raw, Mapping = mapping },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>The value as a person would read it, because the archive is for reading.</summary>
    private static string Describe(RecordValue value) =>
        value.Tags.Count > 0 ? string.Join(", ", value.Tags)
        : value.Location is not null
            ? value.Location.DisplayContext
                ?? string.Create(CultureInfo.InvariantCulture, $"{value.Location.Latitude}, {value.Location.Longitude}")
        : value.TemporalValue ?? value.DateValue ?? value.NumberValue ?? value.TextValue ?? string.Empty;
}
