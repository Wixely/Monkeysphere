using System.Globalization;
using Dapper;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed class SqliteApplicationCommandAudit(MonkeysphereConnectionFactory connections, ICurrentDomain currentDomain) : IApplicationCommandAudit
{
    public async Task RecordAsync(ApplicationCommandEvent entry, CancellationToken cancellationToken = default)
    {
        if (entry.DomainId != currentDomain.Id || entry.Surface is not ("mcp" or "api") ||
            !ValidLabel(entry.Action) || !ValidLabel(entry.Outcome) || entry.CorrelationId.Length > 128)
            throw new DomainValidationException("The command audit metadata is invalid.");
        await using var connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ApplicationCommandAudit (DomainId, Surface, Action, Outcome, CorrelationId, OccurredAtUtc)
            VALUES (@DomainId, @Surface, @Action, @Outcome, @CorrelationId, @OccurredAtUtc);
            DELETE FROM ApplicationCommandAudit WHERE OccurredAtUtc < @Retention;
            DELETE FROM ApplicationCommandAudit WHERE Id NOT IN (SELECT Id FROM ApplicationCommandAudit ORDER BY Id DESC LIMIT 50000);
            """, new
        {
            DomainId = entry.DomainId.ToString("D"),
            entry.Surface,
            entry.Action,
            entry.Outcome,
            entry.CorrelationId,
            OccurredAtUtc = entry.OccurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Retention = entry.OccurredAtUtc.AddDays(-90).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool ValidLabel(string value) => value is { Length: > 0 and <= 64 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_');
}
