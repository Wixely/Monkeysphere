using System.Globalization;
using Dapper;
using DnaX.Hosting;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

/// <summary>
/// Carries a catalogue edit into the domain databases that hold the tag's text: a rename rewrites
/// it, a strip removes it after the tag loses that domain or is deleted.
///
/// The catalogue commits immediately; this is the part that cannot be in the same transaction,
/// because the record text lives in other database files. So both are eventually consistent by
/// construction: between the edit and the drain, a domain still shows the old state. A failed pass
/// leaves the work queued rather than half-applied, and running it again is harmless.
/// </summary>
internal sealed class TagRenameDrainer(
    DomainRegistryConnectionFactory registry,
    IDnaXPaths paths,
    TimeProvider timeProvider)
{
    internal async Task<int> DrainAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection catalogue = await registry.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        PendingRename[] pending = [.. await catalogue.QueryAsync<PendingRename>(new CommandDefinition(
            "SELECT TagId, DomainId, Operation, TargetName FROM TagOutbox ORDER BY EnqueuedAtUtc;",
            cancellationToken: cancellationToken)).ConfigureAwait(false)];

        int applied = 0;
        foreach (PendingRename item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ApplyAsync(item, cancellationToken).ConfigureAwait(false);
                await catalogue.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM TagOutbox WHERE TagId = @TagId AND DomainId = @DomainId;",
                    new { item.TagId, item.DomainId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
                applied++;
            }
            catch (SqliteException)
            {
                // Left queued. A locked or briefly unavailable domain database is retried on the
                // next pass rather than losing the rename, and the attempt is stamped so a blocked
                // item does not hide the others.
                await catalogue.ExecuteAsync(new CommandDefinition(
                    "UPDATE TagOutbox SET LastAttemptAtUtc = @Now WHERE TagId = @TagId AND DomainId = @DomainId;",
                    new
                    {
                        item.TagId,
                        item.DomainId,
                        Now = timeProvider.GetUtcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }

        return applied;
    }

    private async Task ApplyAsync(PendingRename item, CancellationToken cancellationToken)
    {
        Guid domainId = Guid.ParseExact(item.DomainId, "D");
        await using SqliteConnection connection = DomainRegistryConnectionFactory.Create(
            paths.ResolveWritable(DomainStoragePaths.DatabaseRelativePath(domainId)));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (item.Operation == "strip")
        {
            // Matches the identifier where the sync has stamped it and the text where it has not,
            // so a domain that predates the catalogue is still cleaned.
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM RecordTags WHERE TagId = @TagId OR (TagId IS NULL AND Value = @TargetName COLLATE NOCASE);",
                new { item.TagId, item.TargetName }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // A record may already carry the tag the rename is heading towards — rename "londres" to
        // "london" on a record holding both. Renaming would break UNIQUE (RecordId, Value), so the
        // losing row is dropped first and the two tags become one on that record.
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM RecordTags
            WHERE TagId = @TagId
              AND EXISTS (
                  SELECT 1 FROM RecordTags other
                  WHERE other.RecordId = RecordTags.RecordId
                    AND other.Value = @TargetName COLLATE NOCASE
                    AND other.TagId IS NOT @TagId);
            """,
            new { item.TagId, item.TargetName }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        // COLLATE BINARY on the guard, because Value is NOCASE: without it a rename that only
        // changes capitalisation compares equal, updates nothing, and the queue entry is deleted
        // as though it had been applied.
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE RecordTags SET Value = @TargetName WHERE TagId = @TagId AND Value <> @TargetName COLLATE BINARY;",
            new { item.TagId, item.TargetName }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class PendingRename
    {
        public required string TagId { get; init; }
        public required string DomainId { get; init; }
        public required string Operation { get; init; }
        public required string TargetName { get; init; }
    }
}

/// <summary>The public face of the two internal reconcilers, so hosts and tests can drive them.</summary>
internal sealed class TagMaintenance(TagCatalogueSync sync, TagRenameDrainer drainer) : ITagMaintenance
{
    public Task<int> DrainRenamesAsync(CancellationToken cancellationToken = default) =>
        drainer.DrainAsync(cancellationToken);

    public Task<int> AdoptExistingTagsAsync(CancellationToken cancellationToken = default) =>
        sync.RunAsync(cancellationToken);
}
