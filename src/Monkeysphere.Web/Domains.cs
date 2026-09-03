using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Monkeysphere.Core;

namespace Monkeysphere.Web;

internal static class DomainSelection
{
    internal const string CookieName = "Monkeysphere.Domain";
    internal const string HeaderName = "X-Monkeysphere-Domain";
    private const string ProtectionPurpose = "Monkeysphere.DomainSelection.v1";

    internal static IDataProtector Protector(IDataProtectionProvider provider) =>
        provider.CreateProtector(ProtectionPurpose);
}

internal sealed class HttpCurrentDomain : ICurrentDomainScope
{
    private readonly IDomainCatalog _domains;
    private readonly AsyncLocal<Guid?> _override = new();
    private readonly Guid _baseDomainId;

    public HttpCurrentDomain(
        IHttpContextAccessor httpContextAccessor,
        IDataProtectionProvider protectionProvider,
        IDomainCatalog domains)
    {
        _domains = domains;
        HttpContext? context = httpContextAccessor.HttpContext;
        _baseDomainId = Resolve(context, DomainSelection.Protector(protectionProvider), domains);
    }

    public Guid Id => _override.Value ?? _baseDomainId;

    public IDisposable Use(Guid domainId)
    {
        if (!_domains.TryGet(domainId, out _))
        {
            throw new DomainValidationException("Domain was not found.");
        }

        Guid? previous = _override.Value;
        _override.Value = domainId;
        return new SelectionScope(_override, previous);
    }

    private static Guid Resolve(HttpContext? context, IDataProtector protector, IDomainCatalog domains)
    {
        if (context is null)
        {
            return MonkeysphereDomains.DefaultId;
        }

        string? header = context.Request.Headers[DomainSelection.HeaderName].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(header))
        {
            if (!Guid.TryParse(header, out Guid headerId) || !domains.TryGet(headerId, out _))
            {
                throw new DomainValidationException($"{DomainSelection.HeaderName} does not identify an available domain.");
            }

            return headerId;
        }

        if (context.Request.Cookies.TryGetValue(DomainSelection.CookieName, out string? protectedValue))
        {
            try
            {
                string value = protector.Unprotect(protectedValue);
                if (Guid.TryParse(value, out Guid cookieId) && domains.TryGet(cookieId, out _))
                {
                    return cookieId;
                }
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // Invalid or obsolete selections fail closed to the backwards-compatible Default domain.
            }
        }

        return domains.DefaultDomain.Id;
    }

    private sealed class SelectionScope(AsyncLocal<Guid?> selection, Guid? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            selection.Value = previous;
            _disposed = true;
        }
    }
}

public static class DomainEndpoints
{
    public static IEndpointRouteBuilder MapDomainSelection(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/domains/select", async (
            HttpContext context,
            IDomainCatalog domains,
            ICurrentDomainScope currentDomain,
            IPresetService presets,
            IDataProtectionProvider protectionProvider,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            IFormCollection form = await context.Request.ReadFormAsync(cancellationToken);
            if (!Guid.TryParse(form["domainId"].FirstOrDefault(), out Guid domainId) ||
                !domains.TryGet(domainId, out _))
            {
                return Results.BadRequest("Domain was not found.");
            }

            string protectedValue = DomainSelection.Protector(protectionProvider).Protect(domainId.ToString("D"));
            context.Response.Cookies.Append(DomainSelection.CookieName, protectedValue, new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps,
                MaxAge = TimeSpan.FromDays(365),
                Path = "/",
            });

            string returnUrl = form["returnUrl"].FirstOrDefault() ?? "/";
            if (!returnUrl.StartsWith('/') || returnUrl.StartsWith("//", StringComparison.Ordinal))
            {
                returnUrl = "/";
            }

            if (returnUrl.StartsWith("/setup", StringComparison.OrdinalIgnoreCase))
            {
                using IDisposable selection = currentDomain.Use(domainId);
                if ((await presets.GetSetupStatusAsync(cancellationToken)).IsComplete)
                {
                    returnUrl = "/";
                }
            }

            return Results.LocalRedirect(returnUrl);
        }).RequireAuthorization();

        return endpoints;
    }
}
