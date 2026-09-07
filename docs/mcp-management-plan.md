# MCP instance-management implementation plan

- Status: Planned; no implementation completed by this document
- Created and last reviewed: 2026-09-07
- Plan owner: Wixely / Agent
- Next review: 2026-09-14
- Tracking: [roadmap milestones and ongoing feature requirement](roadmap.md)

## Outcome and scope

An authorized MCP client should be able to set up a domain, manage its structures and records, import and export contacts, manage images and relationships, use saved views and projections, configure application settings, and perform supported runtime administration without a browser session. Remote access remains disabled by default and existing read credentials do not gain write privileges.

Application management is the delivery target. Initial deployment, administrator bootstrap, host credentials, service installation/restart, application upgrades, deployment configuration, and offline restore remain operator workflows. Expose useful status and documented next steps for these boundaries. Do not add arbitrary SQL, shell execution, server-path uploads, filesystem browsing, or live restore to achieve parity. Domain deletion/transfer and preset upgrades remain separate roadmap work; assess MCP when those features are designed. Debug reset remains outside the production tool set.

All milestones below are planned. Tool names and scope names are proposed contracts to settle in M0, not claims about currently available tools. Review dates and owners are in the roadmap; each implementation PR must update both milestone status and remaining work.

## Verified starting point

Source inspection on 2026-09-07 found six tools in `src/Monkeysphere.Web/Remote/RemoteModels.cs`: `list_domains`, `list_record_types`, `get_record_type`, `search_records`, `get_record`, and `get_record_relationships`. They use `records.read`; domain-specific calls accept optional `domainId`, defaulting to Default. Record retrieval includes image metadata and relationships, but no image-byte transfer. Field definitions omit configuration needed to construct some valid writes, and values need review for lossless typed editing.

The MCPHub catalog exposed to this planning session lists five Monkeysphere tools, without `list_domains` or the domain parameters. This establishes a catalog mismatch, not its cause. Deployment version and discovery freshness have not been verified. No live data was mutated or integration configuration changed.

The Core services already implement much of the browser functionality. Remote adapters should reuse them. Concurrency across browser/MCP edits, durable retry handling, transferable preview state, asynchronous operation status, and some administrative queries need new application support rather than thin wrappers alone. Current security documentation explicitly excludes remote mutations; update that boundary when writes actually ship.

## Contract and implementation rules

1. Keep tools and remote DTOs in Web, application validation and commands in Core, and transactional persistence in Data. Organize tools by feature rather than growing one monolithic class. Use project-owned DnaX manifest migrations for new persistence. Keep the existing .NET/Blazor deployment and PowerShell tooling.
2. Preserve existing tool names and optional Default-domain behavior for reads. Require explicit `domainId` for new domain mutations. Mark deployment-wide tools clearly and reject inappropriate domain selectors. Resolve every referenced record, field, relationship, upload, and preview within its bound domain.
3. Publish version, effective capabilities/scopes, field-type schemas/configuration, supported actions, bounds, and pagination behavior. Return authoritative entities and revision tokens after writes. Distinguish not found, validation failure, permission denial, stale revision/preview, limit exceeded, and retry conflict with stable structured errors.
4. Define omitted versus null versus empty values before shipping edits. Prefer bounded field-level patches for routine updates; any full replacement must be explicit. Preserve aliases, multiple values, temporal precision/approximation, structured locations, and unknown-type fallback data. Do not force clients to reconstruct typed values from display strings.
5. Enforce scopes server-side on every invocation and file request; tool visibility and annotations are not authorization. Proposed families: `instance.read`, `records.read/write`, `relationships.read/write`, `structure.read/write`, `domains.manage`, `media.read/write`, `contacts.import/export`, `views.read/write`, `calendar.read`, `reminders.read/write`, `settings.read/write`, `backups.read/create/download`, and `remote.admin`. Finalize granularity against DnaX support in M0. Scopes grant actions, not multi-user tenancy; domain isolation still applies to every operation.
6. Extend optimistic concurrency to the shared browser and MCP write paths, with revision checks performed inside mutation transactions. Persist idempotency keys with credential identity, domain, action, request hash, and bounded expiry; replay a committed result for identical retries and reject changed payloads. A process failure after commit must not create duplicates on retry.
7. Preserve existing preview/apply semantics for conversions and merges. New destructive/bulk previews return affected counts, conflicts, and an opaque expiring token bound to the request, credential, domain, and relevant revision. Revalidate permissions and revisions at apply time. Explicit selected IDs and expected revisions suffice for ordinary single-record deletion; require impact review when it removes dependent data.
8. Audit action, outcome, domain, and correlation identifier with bounded redacted metadata. Exclude contact contents, upload bodies, secrets, and sensitive filenames from logs. Document cancellation and retry behavior. Use bounded operation status/progress for work that cannot reliably finish within the configured request deadline.
9. Preserve existing Core limits for search, calendar, graph, map, vCard, and images. Add explicit pagination or truncation to every collection, including currently capped type and relationship discovery. Set transport limits with encoding overhead accounted for; do not embed backup archives in tool JSON.

## M0 - Inventory and contract design

Dependencies: none. Owner: Agent.

- Build a checked-in capability matrix mapping each browser/service workflow to proposed tool, scope, domain/deployment boundary, limits, mutation semantics, tests, and Included/Deferred/Not applicable disposition.
- Inspect the deployed version and MCP discovery read-only when available. Compare direct server discovery with MCPHub discovery to locate the mismatch. Record any required deployment or discovery refresh as an operator action; do not install or reconfigure integrations implicitly.
- Inspect the pinned DnaX package's scope, administration, audit, and MCP extension support. Identify any dependency change before relying on it; separate-repository work needs its own authorized scope.
- Finalize DTOs, proposed permissions, compatibility policy, field patch semantics, concurrency storage, preview storage/expiry, idempotency retention, and file transport in a contract document with request/result examples.
- Add `get_instance_info`/`get_capabilities` and complete domain and field-schema discovery using the agreed read authorization.

Exit criteria: matrix and contracts are reviewable; source tools have discovery/schema tests; existing read calls still work; deployment mismatch is resolved or recorded with evidence and owner; foundational decisions have no unresolved implementation blocker.

## M1 - Mutation foundations

Dependencies: M0. Owner: Agent.

- Extend credential issuance to explicit scope selection, maintaining separate API/MCP credentials, deployment gates, and no automatic elevation of existing credentials.
- Implement shared authorization helpers, revisions, durable retry records, preview lifecycle, structured errors, redacted audit, and bounded operation status where needed.
- Define atomic behavior for batches. Start with one-domain transactional record batches; reject unsupported mixed operations. Preview all items before apply and return committed outcomes or an explicit whole-batch rollback, never ambiguous partial success.
- Document a migration/rollback approach: additive changes, read-only fallback through disabling write access, and offline backup restore when a database downgrade is unsupported.

Exit criteria: integration tests prove scope denial, revoked credentials, expired previews, stale edits, wrong-domain references, rollback, concurrent browser/MCP edits, and replay after restart. No write tool ships before its authorization and retry behavior pass.

## M2 - Basic management and setup

Dependencies: M1. Owner: Agent.

- Add record validation/create/patch/delete and structured search/filter/sort. Introduce bounded preview/apply batches using M1 contracts.
- Add relationship-type discovery and creation, relationship creation/deletion, and bounded listing. Identify edit operations missing from Core and track them explicitly rather than advertising unsupported CRUD.
- Add domain create/rename, setup state, preset catalog/installed state, preset installation, and setup completion including the existing blank-slate choice.
- Expose minimum custom record-type and field creation/attachment so a client can build a useful schema without installed presets. Include field configuration and required-value validation.

Exit criteria: an MCP-only integration scenario creates a fresh domain, completes setup, creates two records with typed values and aliases, links them, edits and searches them, and deletes selected test data. Repeat with a second domain to prove isolation and with read-only credentials to prove denial. Existing browser behavior remains valid.

## M3 - Contact files and images

Dependencies: M2; transfer contract from M0. Owner: Agent.

- Implement bounded uploads using opaque handles, declared type/length, content validation, expiry, and abandoned-upload cleanup. Bind handles to credential and domain. Support streaming/chunking where required by the selected transport; never interpret client filenames as server paths or fetch arbitrary supplied URLs.
- Add vCard preview, paged preview inspection, per-contact create/skip/merge/replace selections, transactional apply, and selected-contact export. Preserve opaque properties, Apple labels, duplicate evidence, and current Person-preset requirements. Apply validates that source records/schema still match the preview; stale previews require regeneration.
- Add image upload, listing, derivative/original download, caption and cover changes, ordering, rotation/crop, and deletion. Preserve existing validation and metadata-stripped derivatives.
- Provide authenticated downloads usable by MCP clients without browser cookies. Enforce permissions and expiry at download time, avoid credentials in URLs, and invalidate access after revocation. Reuse this mechanism for calendar exports and backups.

Exit criteria: MCP-only vCard import/export with duplicates and extensions round-trips semantically; interrupted/repeated apply produces no duplicate import; stale and cross-domain previews fail. Image lifecycle tests cover invalid content, limits, corrections, original retention, revoked download access, and temporary-file cleanup. Demonstrate transfer through the intended client/MCPHub path in a disposable instance.

## M4 - Complete structure lifecycle

Dependencies: M2. Owner: Agent.

- Expose record-type rename/symbol changes, retirement preview/apply, and merge preview/apply.
- Expose reusable-field listing, rename, retirement, usage inspection, merge preview/apply, and conversion preview/apply.
- Preserve required-field behavior, conflict choices, saved-view references, reminders, import provenance, and unknown values. Reuse existing transactional revision fingerprints rather than introducing conflicting remote-only rules.

Exit criteria: remote tests cover compatible merges, conflict policies, unsafe conversion rejection, stale preview rejection, and preservation of views/reminders/import provenance. Browser and MCP results agree for the same commands.

## M5 - Views, projections, and settings

Dependencies: M2; file export depends on M3. Owner: Agent.

- Add saved record-view list/get/create/update/duplicate/delete and execution; add supported graph-view lifecycle and saved coordinates.
- Expose bounded graph queries/neighbour expansion, spatial queries, calendar/upcoming-date queries, and iCalendar export.
- Add reminder create/list/dismiss; dashboard projection and configuration; graph and map settings read/update.
- Keep approximate/coarse dates out of exact calendar entries and preserve geographic approximation. Require an explicit disclosure acknowledgement when enabling external map tiles, matching the browser workflow.
- Classify browser-only preferences and interactions in the capability matrix; persisted layouts and configuration remain in scope even if gestures/theme controls do not need tools.

Exit criteria: a client can save/reopen a filtered view and graph layout, query map/calendar data, manage reminders, and update dashboard settings. Tests cover bounds, truncation, invalid references, date precision, and default-off external requests.

## M6 - Runtime administration and backups

Dependencies: M1 and M3. Owner: Agent; Wixely owns deployment-policy decisions.

- Add instance readiness/version and bounded operational status, backup list/create/validate/download, effective schedule configuration, and execution status. Add shared status queries where the application currently lacks them.
- Expose remote-surface state, redacted audit pagination, and separately scoped activation/deactivation, credential revocation/rotation, and endpoint rotation within deployment gates.
- Prevent administrative tools from granting privileges beyond the caller's allowed administration policy or bypassing the master gate. Define a tested rotation/handoff sequence: credentials are revealed once through an explicit result, never logs; self-revocation/endpoint rotation must disclose that the active connection may stop working. Retain browser/operator recovery access.
- Report deployment-configured backup schedules as read-only in this delivery. Runtime schedule editing requires a separate persistence/configuration-precedence design and roadmap item if adopted. Backup deletion/pruning beyond existing retention likewise requires explicit design rather than a generic filesystem tool.
- Document offline restore and operator-owned actions as capability exclusions with concrete operational instructions.

Exit criteria: least-privilege backup and administration credentials are tested separately; backups remain deployment-wide and validate across multiple domains. Long-running creation has unambiguous completion/retry status. Tests prove credential/endpoint lifecycle behavior and continued denial when deployment policy disables a surface.

## M7 - Verification and release

Dependencies: M3-M6. Owner: Wixely / Agent.

- Run `eng/Build.ps1` for locked restore, Release build, text/supply-chain checks, and the full test suite. Add meaningful Core/Data tests for new semantics and Web tests for real MCP discovery and invocation, not only direct C# method calls.
- Run a disposable-instance MCP scenario through the supported client path covering setup, records, files, structures, views, reminders, settings, backup, restart persistence, and credential revocation. Use fictional data; never use personal contacts as test fixtures.
- Recheck cancellation, concurrency, request limits, storage cleanup, error redaction, and isolation for every new feature family. Extend performance evidence for batches, uploads, and backups without inventing throughput guarantees.
- Verify Windows interactive execution first, then Linux interactive/container execution; rerun relevant service/systemd checks where hosting behavior changed. Record unavailable environments as gaps, not passed support claims. This work introduces no new Python or Node.js tooling.
- Update README, architecture, security/threat model, performance boundaries, verification status, and the versioned tool reference with examples, scopes, compatibility, operational recovery, and excluded capabilities.
- Review the completed capability matrix: every existing workflow has implemented coverage or an explicit reason/owner/review date. Release read additions first, then opt-in basic writes and file workflows, then broader management; gate each increment on its milestone tests. Disable new writes to roll back exposure without granting or changing credentials automatically.

Exit criteria: supported management workflows pass through MCP, current read clients remain compatible, no undocumented coverage gap remains, and release notes distinguish implemented behavior from deferred operator or application features. Publishing or changing a live deployment is a separate operational action.

## Decisions and next actions

| Decision/action | Proposed direction | Owner | Review date |
| --- | --- | --- | --- |
| Source versus exposed-tool mismatch | Inspect server version and discovery before proposing a refresh/deployment | Agent | 2026-09-14 |
| DnaX scope/admin support | Reuse pinned capabilities; identify external dependency work explicitly | Agent | 2026-09-14 |
| Upload/download transport | Choose a bounded authenticated mechanism demonstrated through the intended MCP client; opaque handles for large content | Agent | 2026-09-14 |
| Revision, retry, and preview persistence | Shared transactional application semantics with bounded retention and restart tests | Agent | 2026-09-14 |
| Remote administrative authority | Explicit administrative grant, deployment gates, and tested recovery path | Wixely / Agent | 2026-09-14 |
| Runtime backup-schedule editing | Keep deployment-owned for this delivery; evaluate separately if needed | Wixely | 2026-10-01 |

Remaining work: M0-M7 are unimplemented. Recommended next action: Agent starts M0 with the capability matrix and DnaX inspection, then finalizes the contract and updates this plan with resolved decisions before M1 writes begin.
