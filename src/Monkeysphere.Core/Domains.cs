namespace Monkeysphere.Core;

public sealed record MonkeysphereDomain(
    Guid Id,
    string Name,
    bool IsDefault,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public static class MonkeysphereDomains
{
    public static Guid DefaultId { get; } = Guid.Parse("00000000-0000-7000-8000-000000000001");

    public const int MaximumNameLength = 100;

    public static string NormalizeName(string name)
    {
        string normalized = (name ?? string.Empty).Trim();
        if (normalized.Length is < 1 or > MaximumNameLength)
        {
            throw new DomainValidationException($"Domain name must be between 1 and {MaximumNameLength} characters.");
        }

        return normalized;
    }
}

public interface IDomainCatalog
{
    IReadOnlyList<MonkeysphereDomain> Snapshot { get; }

    MonkeysphereDomain DefaultDomain { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<MonkeysphereDomain> CreateAsync(string name, CancellationToken cancellationToken = default);

    Task<MonkeysphereDomain> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default);

    bool TryGet(Guid id, out MonkeysphereDomain? domain);
}

public interface ICurrentDomain
{
    Guid Id { get; }
}

public interface ICurrentDomainScope : ICurrentDomain
{
    IDisposable Use(Guid domainId);
}
