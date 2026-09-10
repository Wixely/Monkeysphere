using DnaX.RemoteAccess;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

public sealed record RemoteScopeOption(string Scope, string Label, string Description);

public sealed class RemoteCredentialManager(IDnaXRemoteAccessAdministration administration)
{
    public static IReadOnlyList<RemoteScopeOption> AvailableScopes(DnaXRemoteSurface surface) => surface == DnaXRemoteSurface.Mcp
        ? [
            new("instance.read", "Instance discovery", "Read versions, supported tools and request limits."),
            new("contacts.import", "Import contacts", "Upload and validate contact files, then preview and apply reviewed create/skip/merge/replace decisions. Also select application data to discover target domains. Importing does not permit exporting contacts."),
            new("media.write", "Add and remove record images", "Upload image files and attach them to a record, or remove one. Select application data to discover the records involved."),
            new("media.read", "Read record images", "Download record image bytes, including retained originals which can carry camera metadata the display copies remove."),
            new("contacts.export", "Export contacts", "Read explicitly selected Person records out of the deployment as a vCard document. Separate from importing contacts; it grants no record editing and no other data reads."),
            new("domains.manage", "Manage domains", "Create and rename domains. Select application data to discover domain IDs and revisions. Domain deletion is not available remotely."),
            new("records.read", "Application data", "Read domains, structures, records and relationships, including discovery."),
            new("records.write", "Create and edit records", "Validate, create and patch records, including previewed atomic batches. Also select application data to discover and read records."),
            new("records.delete", "Delete records", "Preview and delete records with their dependent data and media. Separate from creating/editing records; select application data to discover and read records."),
            new("relationships.write", "Manage relationships", "Create and remove links between records. Select application data to discover records and relationship types."),
            new("structure.write", "Manage structures", "Create record and relationship types, create and attach reusable fields, install presets and complete onboarding. Select application data to discover existing definitions. Other structure changes are not yet available remotely."),
        ]
        : [new("records.read", "Application data", "Read domains, record types, records and relationships.")];

    public static IReadOnlyList<string> InitialScopes(DnaXRemoteEffectiveSurface surface) =>
        surface.Credential?.Scopes ?? ["records.read"];

    public async Task<DnaXGeneratedCredential> RotateAsync(
        DnaXRemoteSurface surface, long expectedVersion, IReadOnlyCollection<string> selectedScopes,
        CancellationToken cancellationToken = default)
    {
        DnaXRemoteAdministrationState state = await administration.GetStateAsync(cancellationToken).ConfigureAwait(false);
        DnaXRemoteEffectiveSurface effective = surface switch
        {
            DnaXRemoteSurface.Api => state.Api,
            DnaXRemoteSurface.Mcp => state.Mcp,
            _ => throw new DomainValidationException("Unknown remote surface."),
        };
        HashSet<string> allowed = AvailableScopes(surface).Select(option => option.Scope).ToHashSet(StringComparer.Ordinal);
        // Existing unrecognized grants can be preserved or removed, but not introduced by this UI.
        allowed.UnionWith(effective.Credential?.Scopes ?? []);
        string[] selected = selectedScopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (selected.Length == 0 || selected.Any(scope => !allowed.Contains(scope)))
        {
            throw new DomainValidationException("Select at least one supported permission or retain an existing grant.");
        }
        return await administration.RotateCredentialAsync(surface, expectedVersion, selected,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
