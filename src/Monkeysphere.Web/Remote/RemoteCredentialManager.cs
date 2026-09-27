using DnaX.RemoteAccess;
using Monkeysphere.Core;
using Monkeysphere.Web.Security;

namespace Monkeysphere.Web.Remote;

public sealed record RemoteScopeOption(string Scope, string Label, string Description);

public sealed class RemoteCredentialManager(IDnaXRemoteAccessAdministration administration)
{
    public static IReadOnlyList<RemoteScopeOption> AvailableScopes(DnaXRemoteSurface surface) => surface == DnaXRemoteSurface.Mcp
        ? [
            new("instance.read", "Instance discovery", "Read versions, supported tools and request limits."),
            new(BackstageScopes.Backstage, "Backstage records", "See records held back by backstage policy, such as hidden records, through every tool this credential can already use. Unlike the browser, this grant does not expire after 24 hours: it applies on every call until the permission is removed. It grants no reads on its own, and it does nothing while backstage is disabled for the deployment."),
            new("contacts.import", "Import contacts", "Upload and validate contact files, then preview and apply reviewed create/skip/merge/replace decisions. Also select application data to discover target domains. Importing does not permit exporting contacts."),
            new("media.write", "Add and remove record images", "Upload image files and attach them to a record, or remove one. Select application data to discover the records involved."),
            new("media.read", "Read record images", "Download record image bytes, including retained originals which can carry camera metadata the display copies remove."),
            new("contacts.export", "Export contacts", "Read explicitly selected Person records out of the deployment as a vCard document. Separate from importing contacts; it grants no record editing and no other data reads."),
            new("tags.manage", "Manage tags", "Create, rename, recolour and delete the deployment's universal tags, and choose which domains offer them. Separate from structure.write because it writes to the deployment registry rather than one domain, and because dropping a domain or deleting a tag removes that tag from records in every domain that holds it. It grants no other record reads or writes."),
            new("views.manage", "Manage saved views", "Create, edit, copy and delete a domain's saved record and graph views, including a graph's remembered layout. Separate from structure.write, which creates record types and fields and installs presets: a saved view changes nothing about what a record can hold, it only stores a question. It grants no reading either — running a view returns record content and needs records.read."),
            new("backups.read", "Read backup packages", "List the deployment's stored backups and open one to report its format, schema version and contents. Backups are deployment-wide, so this sees that every domain exists and how large each package is, but not what any record says. It does not download a package and cannot restore one."),
            new("backups.write", "Take backups", "Take a backup of the whole deployment on demand. It reads everything in order to write the package, so a deployment large enough will make the call slow. It grants no reading of records and no way to download, prune or restore a package."),
            new("domains.manage", "Manage domains", "Create and rename domains. Select application data to discover domain IDs and revisions. Domain deletion is not available remotely."),
            new("records.read", "Application data", "Read domains, structures, records and relationships, including discovery."),
            new("records.write", "Create and edit records", "Validate, create and patch records, including previewed atomic batches. Also select application data to discover and read records."),
            new("records.delete", "Delete records", "Preview and delete records with their dependent data and media. Separate from creating/editing records; select application data to discover and read records."),
            new("relationships.write", "Manage relationships", "Create and remove links between records. Select application data to discover records and relationship types."),
            new("structure.write", "Manage structures", "Create, rename, retire and merge record types; create, attach, rename, retire, merge and convert reusable fields; create, rename and retire relationship types; install presets and complete onboarding. Retiring keeps what is already recorded, and merging and converting move it rather than dropping it. Select application data to discover existing definitions. It grants no reading of record content: a field's usage is reported as counts, and the records behind a conversion's unconvertible values are named only if this credential can read records anyway."),
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
