namespace Monkeysphere.Core;

public sealed record ApplicationCommandEvent(Guid DomainId, string Surface, string Action, string Outcome, string CorrelationId, DateTimeOffset OccurredAtUtc);

public interface IApplicationCommandAudit
{
    Task RecordAsync(ApplicationCommandEvent entry, CancellationToken cancellationToken = default);
}
