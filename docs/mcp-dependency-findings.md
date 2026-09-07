# MCP dependency findings

- Reviewed: 2026-09-07
- Owner: Agent
- Next review: 2026-09-14
- Scope: read-only inspection of the exact DnaX source revision recorded in [third-party notices](../THIRD-PARTY-NOTICES.md), plus local MCP integration tests

The vendored DnaX 10.0.0-alpha.3 packages originate from commit `af960fa02bb1e7e0ff22850426d899f6559cf64f`. The following findings come from that revision, not the current head of the dependency repository. No dependency packages or external configuration have changed.

| Finding | Source at pinned revision | Consequence for this plan |
| --- | --- | --- |
| Arbitrary nonempty scope strings are trimmed, deduplicated and sorted; matching is exact | `DnaXRemoteAccessService.NormalizeScopes`, `DnaXRemoteAccessPrincipalExtensions.HasDnaXRemoteScope` | Monkeysphere can define its own grants without a DnaX package change; validate the application's allowlist during issuance |
| Credential rotation replaces the credential for one surface; there is one effective credential per API/MCP surface | `DnaXRemoteEffectiveSurface.Credential`, `DnaXRemoteAccessSqliteStore.TryReplaceCredentialAsync` | Access presets configure the one surface credential; simultaneous per-client credentials need separate dependency work |
| The authenticated principal exposes surface, scope and an eight-character credential suffix, not the credential UUID | `DnaXRemoteAccessMiddleware`, `DnaXRemoteClaimTypes` | Durable retry/upload ownership must not use the suffix as a unique credential identity; resolve and bind a verified credential generation before writes ship |
| Default limits are 1 MiB request body, eight concurrent requests per surface, 30-second timeout and 60 requests per identity per minute | `DnaXRemoteLimitOptions` | A complete 5 MiB vCard or 10 MiB image cannot be sent inline under defaults; test chunk or authenticated streaming transfer and account for rate limits |
| MCP uses the SDK HTTP transport with `Stateless = true` | `DnaXRemoteMcpExtensions.AddDnaXRemoteMcp` | Preview/upload/operation state belongs to application persistence and opaque IDs, not an assumed MCP session |
| Remote routing rewrites the selected external MCP route to an internal prefix | `DnaXRemoteAccessMiddleware.Rewrite` | Prototype any transfer route under the authenticated boundary; never assume a browser endpoint accepts an MCP credential |
| Audit records contain transport action/result and correlation data, with no domain field; audit retrieval is a bounded recent list | `DnaXRemoteAuditEvent`, `DnaXRemoteAccessSqliteStore.GetRecentAuditAsync` | Add application audit evidence for domain and actual tool outcome; do not claim cursor pagination or tool-error accuracy from transport status alone |
| Audit defaults are 90 days and 50,000 events | `DnaXRemoteAuditOptions` | Reuse supported retention controls rather than duplicate them |
| Runtime administration uses expected surface versions and respects deployment gates | `IDnaXRemoteAccessAdministration`, `DnaXRemoteAccessService` | Carry expected versions into administration tools; do not bypass startup policy |
| Revoking the only MCP credential makes the route unavailable and returns 404 in the local integration scenario | `RemoteDiscoveryTests` in this repository | Test denied access after revocation against actual surface behavior rather than assuming 401 |

## Decisions ready for implementation

- Keep the pinned package for read discovery and application-defined scope checks.
- Keep legacy `records.read` sufficient for discovery; introduce `instance.read` as a narrow alternative that cannot read records or domains.
- Expose effective request limits from configured DnaX options, never duplicated hard-coded transport defaults.
- Report writes and file transfer as unsupported until their implementations and permission tests are complete.
- Treat scope presets as alternatives for the active surface credential. Do not describe them as independent client accounts.

## Remaining design gates

The [write contract](mcp-write-contract.md) selects an authenticated bearer fingerprint for credential-generation binding, 24-hour durable retry results, 15-minute previews and 256 KiB binary chunks. The local chunk probe passed through the pinned transport and confirmed oversized rejection. Agent must implement these semantics, settle persistence/quota limits and add application audit/revision state before M1/M3 completion. The exposed MCPHub catalog mismatch and live transfers remain unverified against the deployment; local source discovery tests establish only repository behavior. Review: 2026-09-14.
