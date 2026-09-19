namespace Monkeysphere.Core;

public sealed record MonkeysphereDomain(
    Guid Id,
    string Name,
    bool IsDefault,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string Revision = "",
    // Observable only from backstage. The Default domain can never carry this: it is the fallback
    // an invalid selection resolves to, and a hidden fallback would leave an ordinary caller with
    // no domain at all.
    bool IsHidden = false);

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

/// <summary>
/// Every domain of the deployment, hidden ones included. This is the registry's own truth, and it
/// exists for infrastructure that must service every domain regardless of who is asking: startup
/// migration, backup, and the cleanup sweeps. A hidden domain's expired previews still have to be
/// collected, and a backup that silently omitted a domain would be a far worse failure than one
/// that includes it.
///
/// Anything acting on behalf of a caller wants <see cref="IDomainCatalog"/> instead.
/// </summary>
public interface IDomainRegistry
{
    IReadOnlyList<MonkeysphereDomain> All { get; }

    MonkeysphereDomain DefaultDomain { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    bool TryGet(Guid id, out MonkeysphereDomain? domain);

    // Creating and renaming are deployment administration rather than something one caller does
    // differently from another, so they live here. Concealment is the exception: it is a backstage
    // authority question, and so it is asked on IDomainCatalog, which knows who is asking.
    Task<MonkeysphereDomain> CreateAsync(string name, CancellationToken cancellationToken = default);

    Task<MonkeysphereDomain> RenameAsync(Guid id, string name, string? expectedRevision = null, CancellationToken cancellationToken = default);

    Task<MonkeysphereDomain> SetHiddenAsync(Guid id, bool hidden, string? expectedRevision = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// The domains the current caller may observe. Hidden domains are withheld unless the caller holds
/// backstage, and they are withheld the same way an unknown domain is, so that selecting one
/// cannot be used to probe for it.
///
/// This is deliberately the interface with the ordinary name: a surface that enumerates or resolves
/// domains gets the rule by default, and reaching every domain requires deliberately asking for
/// <see cref="IDomainRegistry"/>.
/// </summary>
public interface IDomainCatalog
{
    IReadOnlyList<MonkeysphereDomain> Snapshot { get; }

    /// <summary>Never hidden, so it always resolves. This is what an unusable selection falls back to.</summary>
    MonkeysphereDomain DefaultDomain { get; }

    bool TryGet(Guid id, out MonkeysphereDomain? domain);

    /// <summary>
    /// Hides or reveals a domain. Requires backstage, for the same reason changing a record's
    /// backstage state does: an ordinary caller must not be able to conceal a domain from everyone
    /// else, nor reveal one that was deliberately concealed. It sits here rather than on the
    /// registry precisely because only this view knows who is asking.
    /// </summary>
    Task<MonkeysphereDomain> SetHiddenAsync(Guid id, bool hidden, string? expectedRevision = null, CancellationToken cancellationToken = default);
}

public interface ICurrentDomain
{
    Guid Id { get; }
}

public interface ICurrentDomainScope : ICurrentDomain
{
    /// <summary>
    /// Selects a domain on behalf of a caller. Refuses anything the caller cannot observe, which
    /// includes a hidden domain without backstage, and refuses it exactly as it refuses an unknown
    /// one. Remote surfaces rely on this being the point where an unusable selector fails closed.
    /// </summary>
    IDisposable Use(Guid domainId);

    /// <summary>
    /// Selects any registered domain without asking who is looking. This exists only for the
    /// background sweeps that must service every domain — migration, preview cleanup, media
    /// cleanup — and a hidden domain left unswept would accumulate work forever. It must never be
    /// reachable from a request path; use <see cref="Use"/> there.
    /// </summary>
    IDisposable UseForMaintenance(Guid domainId);
}
