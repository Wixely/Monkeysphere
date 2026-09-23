using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

/// <summary>
/// Relates many records to one other record. The relationship store already creates them one at a
/// time; what this adds is doing so across a selection while reporting what happened to each,
/// because a set of forty will usually contain one that is already related or one the caller
/// cannot see, and a single "done" would be untrue for those.
/// </summary>
internal sealed class SqliteRecordRelationshipCommandStore(
    MonkeysphereConnectionFactory connections,
    IBackstageVisibility visibility) : IRecordRelationshipCommandStore
{
    public async Task<IReadOnlyList<RecordRelationshipChange>> ApplyAsync(
        BulkRelationshipRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // The type and the other end are checked once rather than per record: they are the same
        // for every one of them, and failing them is a failure of the whole request rather than an
        // outcome for one record.
        RelationshipTypeRow type = await connection.QuerySingleOrDefaultAsync<RelationshipTypeRow>(new CommandDefinition("""
            SELECT Id, Directionality, Lifecycle FROM RelationshipTypes WHERE Id = @Id;
            """, new { Id = Key(request.RelationshipTypeId) }, cancellationToken: cancellationToken)).ConfigureAwait(false)
            ?? throw new RecordCommandNotFoundException("Relationship type was not found.");

        if (type.Lifecycle != 0)
        {
            throw new DomainValidationException("The relationship type is retired.");
        }

        bool symmetric = type.Directionality != 0;
        string? otherName = await connection.ExecuteScalarAsync<string?>(new CommandDefinition($"""
            SELECT DisplayName FROM Records r WHERE r.Id = @Id{BackstageFilter.AndVisible(visibility, "r")};
            """, new { Id = Key(request.OtherRecordId) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (otherName is null)
        {
            throw new RecordCommandNotFoundException("The record to relate to was not found.");
        }

        List<RecordRelationshipChange> changes = [];
        foreach (Guid recordId in request.RecordIds)
        {
            changes.Add(await RelateOneAsync(connection, request, recordId, symmetric, now, cancellationToken).ConfigureAwait(false));
        }

        return changes;
    }

    private async Task<RecordRelationshipChange> RelateOneAsync(
        SqliteConnection connection,
        BulkRelationshipRequest request,
        Guid recordId,
        bool symmetric,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();

        // The same visibility predicate as every other read of Records, so this write surface
        // cannot confirm that a record the caller may not see exists.
        string? displayName = await connection.ExecuteScalarAsync<string?>(new CommandDefinition($"""
            SELECT DisplayName FROM Records r WHERE r.Id = @Id{BackstageFilter.AndVisible(visibility, "r")};
            """, new { Id = Key(recordId) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (displayName is null)
        {
            return new(recordId, RecordRelationshipOutcome.NotFound, string.Empty, null);
        }

        if (recordId == request.OtherRecordId)
        {
            // Selecting the record you are relating everything to is an easy thing to do, and
            // reporting it beats a constraint failure that discards the rest.
            return new(recordId, RecordRelationshipOutcome.SameRecord, displayName, null);
        }

        (Guid source, Guid target) = request.SelectedAreSource
            ? (recordId, request.OtherRecordId)
            : (request.OtherRecordId, recordId);

        // A symmetric type stores its two ends in a fixed order so one pair cannot also be stored
        // the other way round; without re-applying that here, relating A to B and later B to A
        // would make two relationships that mean the same thing.
        if (symmetric && source.CompareTo(target) > 0)
        {
            (source, target) = (target, source);
        }

        Guid? existing = await connection.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT Id FROM Relationships
            WHERE RelationshipTypeId = @TypeId AND SourceRecordId = @Source AND TargetRecordId = @Target;
            """, new { TypeId = Key(request.RelationshipTypeId), Source = Key(source), Target = Key(target) },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false) is string found
            && Guid.TryParse(found, out Guid parsed) ? parsed : null;
        if (existing is not null)
        {
            return new(recordId, RecordRelationshipOutcome.AlreadyRelated, displayName, existing);
        }

        Guid id = Guid.CreateVersion7();
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO Relationships
                (Id, RelationshipTypeId, SourceRecordId, TargetRecordId, Note, Expired, ExpiresAtUtc, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@Id, @TypeId, @Source, @Target, @Note, @Expired, @ExpiresAtUtc, @Now, @Now);
            """, new
        {
            Id = Key(id),
            TypeId = Key(request.RelationshipTypeId),
            Source = Key(source),
            Target = Key(target),
            request.Note,
            Expired = request.Expiry?.Expired == true ? 1 : 0,
            ExpiresAtUtc = request.Expiry?.ExpiresAtUtc is { } ends ? Timestamp(ends) : null,
            Now = Timestamp(now),
        }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(recordId, RecordRelationshipOutcome.Created, displayName, id);
    }

    private static string Key(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record RelationshipTypeRow(string Id, long Directionality, long Lifecycle);
}
