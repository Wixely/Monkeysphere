# Security and privacy boundary

Monkeysphere stores sensitive personal information. The single administrator, host administrator, volume administrator, backup operator, and trusted reverse-proxy administrator are inside the deployment trust boundary.

The concrete abuse cases, mitigations, accepted risks, and review triggers are maintained in the [initial-release threat model](threat-model.md).

## Initial controls

- No public registration, tenant system, roles, or anonymous record access.
- The unconfigured login is deliberately `admin` / `admin`. Deployment configuration can override the username and can override the password directly or through a password file; Docker Compose exposes interpolated credential settings for deployment overrides. The effective password is hashed in memory with the ASP.NET Core password hasher and is never persisted or logged in plaintext.
- Cookie authentication, explicit sign-out, CSRF validation, bounded session lifetime, and login rate limiting.
- Authentication before record, field, search, remote administration, API, MCP, or detailed diagnostic operations.
- Field merge and conversion are administrator-only application operations. Both require a read-only impact preview and explicit confirmation; a revision fingerprint rejects stale previews, conversion fails closed when any stored value would lose structure or validation, and the final mutation is transactional.
- Record-type retirement and merge use the same administrator-only preview, explicit confirmation, stale-revision rejection, and transactional boundary. Retirement does not erase records, and merging never silently strengthens required-field rules for existing records.
- Authentication before location queries or retrieval. Coordinates, accuracy, and approximation radii are treated as sensitive record data and are returned only through the existing authenticated application and scoped remote-record boundaries. Map queries use an approximation-aware SQLite R-tree, validate geographic bounds, and enforce a maximum 500-row page. The map-pin editor and multi-record map use vendored OpenLayers code with a local graticule and do not disclose coordinates to an external tile or geocoding provider.
- Authentication before relationship-graph queries or rendering. Search, type filtering, and neighbour expansion execute locally and return at most 500 nodes and 2,000 edges. The vendored Cytoscape.js client makes no external graph-service request, and truncation is disclosed rather than bypassed by the browser.
- Authentication before every image delivery. Uploaded images are byte-, dimension-, pixel-, and format-bounded, decoded before acceptance, stored under opaque paths, and ordinarily rendered through regenerated WebP derivatives with source metadata removed. Retained originals require a separate explicit download action and are returned as non-cacheable attachments.
- Authentication before backup creation, listing, validation, or download. Backup packages are treated as sensitive, returned as private non-cacheable attachments, and contain both databases plus authoritative original media. They exclude configuration, data-protection keys, administrator credentials, temporary files, and regenerable derivatives; operators remain responsible for storage encryption and access control.
- Restore is offline and requires filesystem/operator access rather than browser access. A data-root instance lock prevents restore through the supported command while Monkeysphere is running; staging validation happens before replacement, and the previous databases and media are retained for operator rollback.
- DnaX remote access unavailable by default, with separate credentials, randomized routes, bounded requests, and redacted audit metadata when deliberately enabled.
- Minimal unauthenticated liveness response.
- Response headers deny framing and object embedding, suppress referrers and MIME sniffing, and disable camera, microphone, and geolocation browser features. A complete script/style CSP remains a documented residual risk while Blazor's generated import map and framework boot resources are assessed for nonce/hash support.

## Transport

The application serves HTTP. External confidentiality depends on an operator-managed HTTPS reverse proxy. Forwarded headers are trusted only from explicitly configured proxies or networks; remote access must not be enabled externally until this boundary is configured and tested.

The built-in credential is public and must be overridden before exposing a deployment outside a trusted local environment.

## Known initial non-goals

There is no application-level encryption of SQLite files, original media, exports, or backups. Host and storage administrators can read them. Original media is retained and can be explicitly downloaded by the authenticated administrator; it may contain metadata that the normalized display derivatives remove. Mutating remote operations, arbitrary SQL, shell access, and filesystem browsing are not provided.
