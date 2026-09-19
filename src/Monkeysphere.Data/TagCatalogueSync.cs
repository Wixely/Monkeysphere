using System.Globalization;
using Dapper;
using DnaX.Hosting;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

/// <summary>
/// Brings each domain's stored tag text into the deployment catalogue and stamps the rows with the
/// catalogue identity.
///
/// This cannot be a migration. A migration runs inside one database, and the tags live in the
/// domain databases while the catalogue lives in the registry, so the reconciliation has to happen
/// where both are reachable — at startup, after every domain has been migrated.
///
/// It is idempotent and convergent: running it twice changes nothing, and a domain restored from an
/// older backup is folded back in the next time the application starts.
/// </summary>
internal sealed class TagCatalogueSync(
    DomainRegistryConnectionFactory registry,
    IDomainRegistry domains,
    IDnaXPaths paths,
    TimeProvider timeProvider)
{
    internal async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        int adopted = 0;
        await using SqliteConnection catalogue = await registry.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Every domain, hidden included. A concealed sphere's tags still have to be catalogued or
        // its records would lose their colours and its rename fan-out would skip it.
        foreach (MonkeysphereDomain domain in domains.All)
        {
            await using SqliteConnection connection = DomainRegistryConnectionFactory.Create(
                paths.ResolveWritable(DomainStoragePaths.DatabaseRelativePath(domain.Id)));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            string[] names = [.. await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT DISTINCT Value FROM RecordTags;", cancellationToken: cancellationToken)).ConfigureAwait(false)];
            if (names.Length == 0) continue;

            foreach (string name in names)
            {
                Guid tagId = await EnsureAsync(catalogue, name, domain.Id, cancellationToken).ConfigureAwait(false);

                // Stamp the rows, and normalize the text to the catalogue's spelling at the same
                // time, so two domains that disagreed about casing converge on one answer.
                string canonical = await catalogue.ExecuteScalarAsync<string>(new CommandDefinition(
                    "SELECT Name FROM Tags WHERE Id = @Id;", new { Id = Key(tagId) },
                    cancellationToken: cancellationToken)).ConfigureAwait(false) ?? name;
                adopted += await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE RecordTags SET Value = @Canonical, TagId = @TagId
                    WHERE Value = @Name COLLATE NOCASE AND (TagId IS NULL OR Value <> @Canonical);
                    """,
                    new { Canonical = canonical, TagId = Key(tagId), Name = name },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }

        return adopted;
    }

    private async Task<Guid> EnsureAsync(SqliteConnection catalogue, string name, Guid domainId, CancellationToken cancellationToken)
    {
        string? existing = await catalogue.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Id FROM Tags WHERE Name = @Name;", new { Name = name },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        Guid id;
        if (existing is null)
        {
            id = Guid.CreateVersion7();
            string now = timeProvider.GetUtcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            await catalogue.ExecuteAsync(new CommandDefinition("""
                INSERT INTO Tags (Id, Name, Colour, Icon, CreatedAtUtc, UpdatedAtUtc)
                VALUES (@Id, @Name, @Colour, @Icon, @Now, @Now);
                """,
                new
                {
                    Id = Key(id),
                    Name = name,
                    Colour = TagAppearance.RandomColour(),
                    Icon = TagAppearance.DefaultIcon,
                    Now = now,
                }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        else
        {
            id = Guid.ParseExact(existing, "D");
        }

        await catalogue.ExecuteAsync(new CommandDefinition(
            "INSERT OR IGNORE INTO TagDomains (TagId, DomainId) VALUES (@TagId, @DomainId);",
            new { TagId = Key(id), DomainId = Key(domainId) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return id;
    }

    private static string Key(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
}
