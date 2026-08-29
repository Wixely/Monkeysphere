# Initial-release threat model

- Status: Reviewed for the local initial release
- Reviewed: 2026-08-29
- Review triggers: a write-capable remote operation, public-network deployment guidance, a new external integration, a new upload/archive format, multi-user access, encryption-at-rest claims, or a material dependency update

## Scope and trust boundaries

The protected assets are personal records, relationships, aliases, precise and approximate locations, original and derived images, reminders, imports, exports, backups, administrator sessions, remote-access credentials, and data-protection keys.

The single authenticated administrator and the administrators of the host, persistent volume, backup destination, and trusted HTTPS reverse proxy are inside the deployment trust boundary. A browser, network client, uploaded file, imported vCard, restore package, remote API/MCP caller, and dependency package are untrusted until checked at their applicable boundary. Internet exposure is not a safe default because the documented `admin` / `admin` credential is intentionally convenient for a local first run.

Data crosses five principal boundaries:

1. Browser to Blazor/HTTP host through cookie authentication and antiforgery protection.
2. Optional API or MCP client to DnaX remote access through its independent route, credential, scope, and limits.
3. Application services to the application and remote-access SQLite databases.
4. Application services to private media, backup, key, temporary, and rollback directories beneath the configured data root.
5. Operator-managed reverse proxy and backup storage, whose confidentiality and access controls are outside the application.

## Threat review

| ID | Threat and impact | Implemented controls | Residual risk / required operation |
| --- | --- | --- | --- |
| TM-01 | The public default credential permits trivial takeover. | Configuration accepts a direct password or password file; blank or conflicting configuration fails startup. Documentation calls out the default. | **High if exposed.** Replace `admin` / `admin` before any untrusted network can connect. This remains an explicit product tradeoff. |
| TM-02 | Credential guessing or username discovery. | Login is limited to five attempts per remote address per minute. PBKDF2 uses 210,000 iterations, and a wrong username still performs password verification work. | Distributed guessing and clients behind one proxy remain possible. Configure only known forwarding proxies and add an upstream limit for public exposure. |
| TM-03 | Session theft, fixation, or indefinite reuse. | HTTP-only, SameSite=Strict, non-persistent cookies; 30-minute sliding expiry; 12-hour absolute lifetime; a new identity is issued only after login. Cookies become Secure when a trusted proxy reports HTTPS. | Plain HTTP permits network theft. Use HTTPS at the trusted reverse proxy whenever the network is not fully trusted. |
| TM-04 | Cross-site request forgery changes private data. | Login/logout validate antiforgery tokens; Blazor server mutations run in an authenticated circuit; SameSite=Strict reduces cross-site cookie sending. | Re-review every new conventional HTTP mutation and require antiforgery explicitly. Remote mutations are absent. |
| TM-05 | Authentication bypass exposes records or diagnostics. | A fallback authorization policy protects all endpoints unless deliberately anonymous. Only static assets, login, and minimal live/ready responses are anonymous. Automated route tests cover sensitive pages and downloads. | Maintain a deny-by-default review for every new endpoint; health responses must remain content-free. |
| TM-06 | Remote API/MCP credentials broaden access or cross surfaces. | DnaX remote access, API, and MCP are deployment-disabled by default; activation uses separate credentials, randomized rotatable routes, one read scope, bounded operations, and redacted audit records. App queries go through Core rather than SQL/files. | DnaX is pinned to a prerelease. Keep remote access disabled unless required and rerun its boundary tests on every update. Write, media, export, backup, configuration, SQL, shell, and filesystem operations remain excluded. |
| TM-07 | Injection changes queries or stored data. | Dapper queries are parameterized; sort/filter/type selections are validated or selected from application-owned definitions; open field types fall back to text rather than executable behavior. | Dynamic query additions require allow-listed identifiers and integration tests. SQLite files remain directly mutable by a trusted host administrator. |
| TM-08 | Malicious images exhaust resources or exploit decoding. | Upload size, count, dimensions, pixel count, signatures, and formats are bounded. SkiaSharp decodes before acceptance; opaque paths are generated; browser derivatives are normalized WebP without source metadata. | Image decoders remain a native-code dependency. Keep versions patched; do not add formats without fuzz/boundary review. Originals may retain sensitive metadata. |
| TM-09 | Malicious imports or archives cause parser abuse, path traversal, or partial writes. | vCard input is UTF-8 and bounded, previewed, explicitly resolved, and applied transactionally. Backup restore rejects unsafe/duplicate/unmanifested paths, checks lengths and SHA-256, validates SQLite/DnaX/media consistency in staging, and keeps rollback data. | Checksums detect corruption but are not signatures. Anyone controlling both archive and manifest can replace content; restore packages must come from trusted storage. |
| TM-10 | Stored text executes in the administrator browser or a third-party client. | Razor encodes rendered strings. Map and graph adapters pass record data as library data rather than HTML. Security headers deny framing, object embedding, referrer disclosure, and unused powerful features. | A complete script/style CSP is not yet enforced because Blazor emits an import map and framework boot resources that require a nonce/hash design. Revisit before claiming hardened public-browser deployment. |
| TM-11 | External services learn private data. | Browser assets are vendored; map rendering uses a local graticule; no tile, geocoder, analytics, font, graph, notification, or hosted calendar request is made. A build check rejects public HTTP references in first-party browser entry points. | Operator-added proxies or future integrations can disclose metadata and need separate consent and threat review. |
| TM-12 | Data, exports, or backups are read from disk. | Data directories are configurable; Docker runs as a non-root user; systemd guidance uses a dedicated account and filesystem hardening; browser downloads are authenticated and no-store. Keys and credentials are excluded from backups. | **Accepted boundary:** there is no application-level encryption at rest. Host, volume, and backup administrators can read private content. Use encrypted storage and restrict filesystem permissions. |
| TM-13 | Sensitive values leak through logs, caches, filenames, or errors. | Remote audit data is bounded/redacted; sensitive downloads use private no-store and `nosniff`; stored paths use UUIDs; production exceptions use the generic error handler. | Original download filenames and operator-visible paths may contain user-supplied names. Avoid publishing logs or data directories and review diagnostics before sharing. |
| TM-14 | Expensive queries, uploads, backups, or scheduled work deny service. | Search, views, calendar, maps, graph, API, imports, exports, images, and backup contents have explicit limits. Backup retention is bounded and applied after successful creation. One process locks a data root. | The authenticated administrator or a compromised session can fill storage. Monitor free space and back up to a separately controlled location. |
| TM-15 | A compromised or substituted dependency executes trusted code. | NuGet versions and dependency graphs are locked; DnaX packages and browser assets are vendored with documented SHA-256 hashes and licenses; local verification enforces hashes, central versioning, notices, and absence of runtime CDN URLs. | Hashes prove repository consistency, not upstream trust. Review release provenance and current advisories before each release. |

## Security acceptance and residual risks

The local initial release accepts the trusted-host/no-encryption boundary, the convenient default credential for non-exposed first run, operator-managed HTTPS, checksum-only backups, and the absence of a complete script/style CSP. These are deployment constraints, not hidden guarantees.

Before a public release candidate, rerun the full tests and `eng\VerifySupplyChain.ps1 -AuditVulnerabilities`, review the complete outgoing Git history and binaries, replace the default credential in every exposed deployment, and verify the reverse proxy, Windows Service, Docker, Linux/systemd, and recovery procedures in their real environments.
