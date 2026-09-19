using System.Globalization;
using Dapper;
using DnaX.Hosting;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

/// <summary>
/// The tag catalogue lives in the domain registry, because a tag has to be recognisable from any
/// domain for typing an existing label to enable it there rather than create a second tag. Each
/// domain's own database still stores the tag's text on the record, so a domain reads correctly on
/// its own and losing the registry costs colours, icons and membership rather than data.
///
/// Reads are filtered by backstage visibility: a hidden domain's membership is withheld, so this
/// page cannot be used to discover that a concealed sphere exists.
/// </summary>
internal sealed class SqliteTagCatalogue(
    DomainRegistryConnectionFactory registry,
    IDomainRegistry domains,
    IBackstageVisibility visibility,
    IDnaXPaths paths,
    TagRenameDrainer drainer,
    TimeProvider timeProvider) : ITagCatalogue
{
    public async Task<IReadOnlyList<TagDefinition>> ListAsync(Guid? domainId = null, CancellationToken cancellationToken = default)
    {
        HashSet<Guid> observable = ObservableDomains();
        if (domainId is Guid requested && !observable.Contains(requested))
        {
            // Indistinguishable from a domain that does not exist, as domain selection already is.
            throw new DomainValidationException("Domain was not found.");
        }

        await using SqliteConnection connection = await registry.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TagDefinition> all = await ReadAsync(connection, null, cancellationToken).ConfigureAwait(false);
        return all
            .Select(tag => tag with { DomainIds = [.. tag.DomainIds.Where(observable.Contains)] })
            .Where(tag => domainId is not Guid selected || tag.DomainIds.Contains(selected))
            // A tag whose only memberships are hidden reads as an unattached tag rather than
            // disappearing, because the administrator can still see and tidy it.
            .ToArray();
    }

    public async Task<TagDefinition?> FindAsync(string name, CancellationToken cancellationToken = default)
    {
        string normalized = NormalizeName(name);
        await using SqliteConnection connection = await registry.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TagDefinition> found = await ReadAsync(connection, normalized, cancellationToken).ConfigureAwait(false);
        return found.Count == 0 ? null : found[0];
    }

    public async Task<TagDefinition> EnsureAsync(string name, Guid domainId, CancellationToken cancellationToken = default)
    {
        string normalized = NormalizeName(name);
        // Without this an arbitrary identifier creates a TagDomains row pointing at no domain,
        // which nothing cleans up, and a caller without backstage could attach a tag to a hidden
        // domain. Refused the same way an unknown domain is, so neither can be told apart.
        if (!ObservableDomains().Contains(domainId))
        {
            throw new DomainValidationException("Domain was not found.");
        }

        await using SqliteConnection connection = await registry.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        string now = Timestamp(timeProvider.GetUtcNow());
        // COLLATE NOCASE on Name means an existing tag is found whatever case was typed, and the
        // stored spelling wins. That is the whole point: one label, one tag, one spelling.
        string? existingId = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Id FROM Tags WHERE Name = @Name;", new { Name = normalized }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        Guid id;
        if (existingId is null)
        {
            id = Guid.CreateVersion7();
            // ON CONFLICT rather than a bare insert: the check above and this write are not one
            // atomic step, so a concurrent first use of the same name must resolve to whichever
            // row won rather than failing the caller's record save with a constraint error.
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO Tags (Id, Name, Colour, Icon, CreatedAtUtc, UpdatedAtUtc)
                VALUES (@Id, @Name, @Colour, @Icon, @Now, @Now)
                ON CONFLICT (Name) DO NOTHING;
                """,
                new
                {
                    Id = Key(id),
                    Name = normalized,
                    Colour = TagAppearance.RandomColour(),
                    Icon = TagAppearance.DefaultIcon,
                    Now = now,
                }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            // Re-read rather than trusting the identifier just generated: the insert may have
            // been the one that lost.
            id = Guid.ParseExact(await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT Id FROM Tags WHERE Name = @Name;", new { Name = normalized }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false) ?? Key(id), "D");
        }
        else
        {
            id = Guid.ParseExact(existingId, "D");
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR IGNORE INTO TagDomains (TagId, DomainId) VALUES (@TagId, @DomainId);",
            new { TagId = Key(id), DomainId = Key(domainId) }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        IReadOnlyList<TagDefinition> read = await ReadAsync(connection, normalized, cancellationToken, transaction).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return read[0];
    }

    public async Task<TagDefinition> SetAppearanceAsync(Guid id, string colour, string icon,
        string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        string normalizedColour = TagAppearance.NormalizeColour(colour);
        string normalizedIcon = TagAppearance.NormalizeIcon(icon);
        await using SqliteConnection connection = await registry.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RequireRevisionAsync(connection, transaction, id, expectedRevision, cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE Tags SET Colour = @Colour, Icon = @Icon, UpdatedAtUtc = @Now WHERE Id = @Id;
            """,
            new { Id = Key(id), Colour = normalizedColour, Icon = normalizedIcon, Now = Timestamp(timeProvider.GetUtcNow()) },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        TagDefinition result = (await ReadAsync(connection, null, cancellationToken, transaction, id).ConfigureAwait(false))[0];
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<TagDefinition> RenameAsync(Guid id, string name, string? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        string normalized = NormalizeName(name);
        await using SqliteConnection connection = await registry.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RequireRevisionAsync(connection, transaction, id, expectedRevision, cancellationToken).ConfigureAwait(false);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE Tags SET Name = @Name, UpdatedAtUtc = @Now WHERE Id = @Id;",
                new { Id = Key(id), Name = normalized, Now = Timestamp(timeProvider.GetUtcNow()) },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            // Merging two tags is a different operation with different consequences for records,
            // so renaming onto an existing name is refused rather than quietly combining them.
            throw new DomainValidationException("A tag with that name already exists.", exception);
        }

        // Queued per domain rather than written here: the record text lives in other database
        // files and no transaction spans them. The queue is keyed so repeated renames collapse
        // onto the newest target.
        IReadOnlyList<Guid> members = await ReadDomainIdsAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        foreach (Guid member in members)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO TagOutbox (TagId, DomainId, Operation, TargetName, EnqueuedAtUtc)
                VALUES (@TagId, @DomainId, 'rename', @TargetName, @Now)
                ON CONFLICT (TagId, DomainId) DO UPDATE SET
                    Operation = excluded.Operation,
                    TargetName = excluded.TargetName,
                    EnqueuedAtUtc = excluded.EnqueuedAtUtc,
                    LastAttemptAtUtc = '';
                """,
                new { TagId = Key(id), DomainId = Key(member), TargetName = normalized, Now = Timestamp(timeProvider.GetUtcNow()) },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        TagDefinition result = (await ReadAsync(connection, null, cancellationToken, transaction, id).ConfigureAwait(false))[0];
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<TagDefinition> SetDomainsAsync(Guid id, IReadOnlyList<Guid> domainIds,
        string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        HashSet<Guid> requested = [.. domainIds];
        HashSet<Guid> observable = ObservableDomains();
        if (!requested.IsSubsetOf(observable))
        {
            throw new DomainValidationException("Domain was not found.");
        }

        await using SqliteConnection connection = await registry.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        Guid[] removed;
        await using (SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await RequireRevisionAsync(connection, transaction, id, expectedRevision, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<Guid> current = await ReadDomainIdsAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
            // A membership the caller cannot observe is left alone rather than dropped, or a
            // tidy-up done without backstage would silently detach a hidden domain's tag.
            removed = [.. current.Where(domain => observable.Contains(domain) && !requested.Contains(domain))];

            foreach (Guid domain in removed)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM TagDomains WHERE TagId = @TagId AND DomainId = @DomainId;",
                    new { TagId = Key(id), DomainId = Key(domain) }, transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                _ = await EnqueueStripAsync(connection, transaction, id, domain, cancellationToken).ConfigureAwait(false);
            }

            foreach (Guid domain in requested.Where(domain => !current.Contains(domain)))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT OR IGNORE INTO TagDomains (TagId, DomainId) VALUES (@TagId, @DomainId);",
                    new { TagId = Key(id), DomainId = Key(domain) }, transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // Removing a domain removes the tag from its records. Deliberately destructive, and the
        // page confirms against CountUsageAsync before reaching here. The work is queued with the
        // membership change rather than done after it, so an interruption leaves it pending rather
        // than leaving records carrying a tag the catalogue no longer lists.
        if (removed.Length > 0) await drainer.DrainAsync(cancellationToken).ConfigureAwait(false);

        return (await ReadAsync(connection, null, cancellationToken, null, id).ConfigureAwait(false))[0];
    }

    public async Task DeleteAsync(Guid id, string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await registry.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        string? name = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Name FROM Tags WHERE Id = @Id;", new { Id = Key(id) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await using (SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await RequireRevisionAsync(connection, transaction, id, expectedRevision, cancellationToken).ConfigureAwait(false);
            foreach (Guid domain in await ReadDomainIdsAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false))
            {
                _ = await EnqueueStripAsync(connection, transaction, id, domain, cancellationToken).ConfigureAwait(false);
            }

            // TagDomains cascades away with the tag; the queued strips survive because the outbox
            // deliberately holds no foreign key to Tags, and the name it carries is what the
            // drainer matches on for rows the sync never stamped.
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM Tags WHERE Id = @Id;", new { Id = Key(id) }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await drainer.DrainAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task<int> EnqueueStripAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid tagId, Guid domainId, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO TagOutbox (TagId, DomainId, Operation, TargetName, EnqueuedAtUtc)
            SELECT @TagId, @DomainId, 'strip', Name, @Now FROM Tags WHERE Id = @TagId
            ON CONFLICT (TagId, DomainId) DO UPDATE SET
                Operation = 'strip',
                EnqueuedAtUtc = excluded.EnqueuedAtUtc,
                LastAttemptAtUtc = '';
            """,
            new { TagId = Key(tagId), DomainId = Key(domainId), Now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
            transaction, cancellationToken: cancellationToken));

    public async Task<IReadOnlyDictionary<Guid, int>> CountUsageAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await registry.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        string? name = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Name FROM Tags WHERE Id = @Id;", new { Id = Key(id) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (name is null) return new Dictionary<Guid, int>();

        IReadOnlyList<Guid> members = await ReadDomainIdsAsync(connection, null, id, cancellationToken).ConfigureAwait(false);
        HashSet<Guid> observable = ObservableDomains();
        Dictionary<Guid, int> counts = [];
        foreach (Guid domain in members.Where(observable.Contains))
        {
            await using SqliteConnection domainConnection = OpenDomain(domain);
            await domainConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            counts[domain] = await domainConnection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM RecordTags WHERE TagId = @TagId OR (TagId IS NULL AND Value = @Name COLLATE NOCASE);",
                new { TagId = Key(id), Name = name }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        return counts;
    }

    private HashSet<Guid> ObservableDomains() =>
    [
        .. domains.All
            .Where(domain => !domain.IsHidden || visibility.IncludeBackstageRecords)
            .Select(domain => domain.Id),
    ];

    private SqliteConnection OpenDomain(Guid domainId) =>
        DomainRegistryConnectionFactory.Create(paths.ResolveWritable(DomainStoragePaths.DatabaseRelativePath(domainId)));

    private static async Task RequireRevisionAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, string? expectedRevision, CancellationToken cancellationToken)
    {
        string? revision = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Revision FROM Tags WHERE Id = @Id;", new { Id = Key(id) }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (revision is null) throw new DomainValidationException("Tag was not found.");
        if (expectedRevision is not null && !string.Equals(revision, expectedRevision, StringComparison.Ordinal))
        {
            throw new ConcurrencyConflictException("The tag changed. Reload it before saving.");
        }
    }

    private static async Task<IReadOnlyList<Guid>> ReadDomainIdsAsync(SqliteConnection connection,
        SqliteTransaction? transaction, Guid id, CancellationToken cancellationToken) =>
        [.. (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DomainId FROM TagDomains WHERE TagId = @TagId;", new { TagId = Key(id) }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).Select(value => Guid.ParseExact(value, "D"))];

    private static async Task<IReadOnlyList<TagDefinition>> ReadAsync(SqliteConnection connection, string? name,
        CancellationToken cancellationToken, SqliteTransaction? transaction = null, Guid? id = null)
    {
        IEnumerable<TagRow> rows = await connection.QueryAsync<TagRow>(new CommandDefinition("""
            SELECT Id, Name, Colour, Icon, CreatedAtUtc, UpdatedAtUtc, Revision
            FROM Tags
            WHERE (@Name IS NULL OR Name = @Name) AND (@Id IS NULL OR Id = @Id)
            ORDER BY Name COLLATE NOCASE;
            """, new { Name = name, Id = id is Guid value ? Key(value) : null }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        IEnumerable<(string TagId, string DomainId)> memberships = await connection.QueryAsync<(string, string)>(
            new CommandDefinition("SELECT TagId, DomainId FROM TagDomains;", transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        ILookup<string, Guid> byTag = memberships.ToLookup(
            entry => entry.TagId, entry => Guid.ParseExact(entry.DomainId, "D"), StringComparer.OrdinalIgnoreCase);

        return
        [
            .. rows.Select(row => new TagDefinition(
                Guid.ParseExact(row.Id, "D"),
                row.Name,
                row.Colour,
                row.Icon,
                [.. byTag[row.Id]],
                ParseTimestamp(row.CreatedAtUtc),
                ParseTimestamp(row.UpdatedAtUtc),
                row.Revision)),
        ];
    }

    private static string NormalizeName(string name) => RecordTagRules.Normalize([name]) is [string single]
        ? single
        : throw new DomainValidationException("A tag cannot be empty.");

    private static string Key(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class TagRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Colour { get; init; }
        public required string Icon { get; init; }
        public required string CreatedAtUtc { get; init; }
        public required string UpdatedAtUtc { get; init; }
        public string Revision { get; init; } = string.Empty;
    }
}
